// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

/*
	It is often helpful to add a title to each call stack (even when missing).
	However, WPA can't aggregate same call stacks when the titles have different PIDs, types, ...
	Nonetheless, middle stacks have titles as separators.
*/
// #define StackTitle

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols; // IStackDataSource, IStackSnapshot
using NetBlameCustomDataSource.Link;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text; // StringBuilder
using static NetBlameCustomDataSource.Util;
using IDVal = System.Int32; // Process/ThreadID
using StackFrame = Microsoft.Windows.EventTracing.Symbols.StackFrame;
using IStackSnapshotAccessProvider = Microsoft.Performance.SDK.Processing.ICollectionAccessProvider<Microsoft.Windows.EventTracing.Symbols.IStackSnapshot, string>;

/*
	CALL STACKS
	We expect ETW to have call stacks with at least the following events:

	Winsock:
		AfdCreate

	WinINet:
		RequestCreatedA
		SendRequest_Start

	WebIO/WinHTTP:
		SessionCreate
		RequestCreate

	WinHTTP Thread Pool:
		ThreadAction_Queue

	Windows Thread Pool:
		ThreadPoolCallbackEnqueue
		ThreadPoolSetTimer

	Office Task/Thread Pool:
		TPWorkObjectCreate
		TPWorkerStartExec
		TPTimerSubmit

	Office Idle Manager
		IdleUpdateQueued
		IdleRegisterTask
*/

namespace NetBlameCustomDataSource.Stack
{
	static class PID { public const IDVal Unknown = -1; } // Process ID
	static class TID { public const IDVal Unknown = -1; } // Thrread ID
	static class PROC { public const IDVal Unknown = -1; } // Processor ID

	public class MyStackSnapshot : IStackSnapshot
	{
		public struct Attributes
		{
			public IDVal tidEnqueue;
			public IDVal tidExec;
			public int cCut;
			public XLinkType type;
			public string strSubType;
		}

		public readonly IStackSnapshot[] rgStack;
		public readonly Attributes[] rgAttrib;

		// If there is exactly one Stack then rgStack[0] == rgStack[^1].
		// But that Stack cannot be BOTH the First AND the Last Stack.
		public bool IsOneStack => rgStack?.Length == 1 && rgStack[0] != null;

		// The stack which first enqueued the work item, usually on the main thread.
		// If there is only one stack then let it be StackLast.
		public IStackSnapshot StackFirst => IsOneStack ? null : rgStack?[0];

		// The stack which actually dispatched the network request, usually via threadpool worker.
		public IStackSnapshot StackLast => rgStack?[^1];

		public IStackSnapshot[] StackChainExport() => rgStack;

		private static MyStackSnapshot s_stackSnapshotNullFlag = new MyStackSnapshot();

		public MyStackSnapshot() { } // no stacks

		public IDVal TidFirst
		{
			get
			{
				AssertImportant(this.StackFirst == null || this.StackFirst.ThreadId == this.rgAttrib[0].tidEnqueue);
				return this.StackFirst?.ThreadId ?? this.rgAttrib?[0].tidEnqueue ?? TID.Unknown;
			}
		}

		/*
			Populate the stack chains and their attributes.
		*/
		public MyStackSnapshot(in IStackSnapshot stack, in Link.XLink xlink, IDVal tidStack)
		{
			// We have the stacks in the TaskItem.XLink chain, plus the one stack param.
			uint cStack = xlink.depth + 1;
			AssertCritical((cStack == 1) == (xlink.taskLinkNext == null));

			this.rgStack = new IStackSnapshot[cStack];
			this.rgAttrib = new Attributes[cStack];

		/*	For each Thread Pool item we've logged these attributes:
			- Stack of the ENQUEUE operation
			- ThreadIDs of the ENQUEUE and of the EXECUTE operations
			- Type and SubType of the Thread Pool operation
			Each stack contains both the EXECUTE of the previous item and the chained ENQUEUE of the next.
			Except that the first stack should start at WinMain, and the last stack ends with the Network event.
			Although the Thread Pool Type and SubType are associated with the ENQUEUE operation in the code,
			in the UI we want to associate them with the EXECUTE operation, which is near the root of the next stack.
		*/
			uint depth = 0;
			XLinkType typeNext = xlink.typeNext;
			for (Tasks.TaskItem taskNext = xlink.taskLinkNext; taskNext != null; taskNext = taskNext.xlink.taskLinkNext)
			{
				depth = taskNext.xlink.depth;
				AssertImportant(depth == (taskNext.xlink.taskLinkNext?.xlink.depth + 1 ?? 0)); // Sequential!
				this.rgStack[depth] = taskNext.stack;
				this.rgAttrib[depth].tidEnqueue = taskNext.tidCreate;
				this.rgAttrib[depth].tidExec = taskNext.tidExec;
				this.rgAttrib[depth + 1].type = typeNext;
				this.rgAttrib[depth + 1].strSubType = taskNext.Info.SubTypeName;
				typeNext = taskNext.xlink.typeNext;
			}
			AssertImportant(depth == 0);
			AssertImportant(typeNext == XLinkType.None);

			AssertImportant(this.rgAttrib[0].type == XLinkType.None);
			AssertImportant(this.rgAttrib[0].strSubType == null);
			AssertImportant(this.rgAttrib[^1].cCut == 0);
			AssertImportant(this.rgAttrib[^1].tidEnqueue == 0);
			AssertImportant(this.rgAttrib[^1].tidExec == 0);

			this.rgAttrib[^1].tidEnqueue = TID.Unknown;
			this.rgAttrib[^1].tidExec = TID.Unknown;

			AssertCritical(this.StackLast == null);

			// This is the final stack where the network action occurs.
			this.rgStack[^1] = stack;
			this.rgAttrib[^1].tidExec = tidStack;
			AssertImportant(FImplies(stack != null, tidStack != TID.Unknown));

			// Mark and remove redundancies in the set of stacks.
			if (OptimizeStack())
			{
				this.rgStack = this.rgStack.Where(s => s != s_stackSnapshotNullFlag).ToArray();
				this.rgAttrib = this.rgAttrib.Where(a => a.cCut >= 0).ToArray();
				AssertCritical(this.rgStack.Length == this.rgAttrib.Length);
			}
		}

		bool OptimizeStack()
		{
			// Don't consider the final, network stack.
			int cOpt = DoOptimizeStack(0, this.rgStack.Length - 2);
			return cOpt > 0;
		}

		/*
			Recursively mark redundant stack cycles for deletion.
		*/
		int DoOptimizeStack(int iFirst, int iLast)
		{
			int span = iLast - iFirst;

			if (span <= 0)
				return 0;

			if (span == 1 && this.rgStack[iFirst].Hash() != this.rgStack[iLast].Hash())
				return 0;

			// Map a stack hash to the most recently observed index of that stack.

			Dictionary<int, int> hashSpan = new Dictionary<int, int>(span + 1);

			bool fOptimizeAbove = false, fOptimizeBelow = false;

			// Loop through the stacks finding the longest redundant cycle.

			int deltaMax = 0, countMax = 0, iMax = 0;
			int delta = 0, count = 0, i;

			for (i = iFirst; i <= iLast; i++)
			{
				int hash = this.rgStack[i].Hash();
				int index = i - delta;

				if (delta == 0)
				{
					if (hashSpan.TryGetValue(hash, out index) && hash != 0) // Don't match null stacks.
					{
						delta = i - index;
						count = 1;
					}
				}
				else if (this.rgStack[index].Hash() == hash && hash != 0)
				{
					++count;
				}
				else if (!hashSpan.TryGetValue(hash, out index))
				{
					delta = 0;
				}
				else if (delta == i - index)
				{
					++count;
				}
				else
				{
					if (count >= delta)
					{
						if (count > countMax)
						{
							if (countMax > 0 && iMax - countMax < i - count)
								fOptimizeBelow = true;

							countMax = count;
							iMax = i;
							deltaMax = delta;
						}
						else
						{
							fOptimizeAbove = true;
						}
					}
					delta = i - index;
					count = 1;
				}

				hashSpan[hash] = i;
			}

			if (delta > 0 && count >= delta)
			{
				if (count > countMax)
				{
					if (countMax > 0 && iMax - countMax < i - count)
						fOptimizeBelow = true;

					countMax = count;
					iMax = i;
					deltaMax = delta;
				}
				else
				{
					fOptimizeAbove = true;
				}
			}

			if (countMax <= 0) return 0;

			/*
			Transform:   0  1  2  3  4  5  6  7  8  9 10 11 12 13
				WinMain->a->b->c->d->e->f->d->e->f->d->e->f->g->h->Network
			into this:   0  1  2  3  4  5 12 13
				WinMain->a->b->c->d->e->f->g->h->Network

			In this case: i = 12, count = 6, delta = 3 => sizeof{d,e,f}
			And below: start = 6 (after first {d,e,f}), size = 6 (2 more sets of 3), offset = 0
			*/
			int offset = countMax % deltaMax;
			int size = countMax - offset;
			int start = iMax - size;
			AssertCritical(start > 0);
			AssertCritical(start + size < this.rgStack.Length);

			// Count the cut elements at the end of the coalesced range.
			AssertImportant(this.rgAttrib[start].cCut >= 0);
			for (i = start + size; i < this.rgAttrib.Length; ++i)
			{
				if (this.rgAttrib[i].cCut >= 0)
				{
					this.rgAttrib[i].cCut += size + this.rgAttrib[start].cCut;
					break;
				}
			}
			AssertImportant(i < this.rgAttrib.Length); // break, not fall out

			// Mark stack entries, attributes for deletion.

			for (i = start; i < start+size; ++i)
			{
				AssertImportant(FImplies(i > start, this.rgAttrib[i].cCut == 0));
				this.rgAttrib[i].cCut = -1;
				this.rgStack[i] = s_stackSnapshotNullFlag;
			}

			if (fOptimizeBelow)
				size += DoOptimizeStack(iFirst, start-1);

			if (fOptimizeAbove)
				size += DoOptimizeStack(start+size, iLast);

			return size;
		} // OptimizeStack


		// Implement IStackSnapshot on StackLast
		public int ProcessId { get => this.StackLast?.ProcessId ?? PID.Unknown; }
		public int ThreadId { get => this.StackLast?.ThreadId ?? TID.Unknown; }
		public TraceTimestamp Timestamp { get => this.StackLast?.Timestamp ?? default; }
		public IProcess Process { get => this.StackLast?.Process ?? default; }
		public IThread Thread { get => this.StackLast?.Thread ?? default; }
		public int Processor { get => this.StackLast?.Processor ?? PROC.Unknown; }
		public IReadOnlyList<IStackFrameTag> GetStackFrameTags(IStackTagMapper mapper) => this.StackLast?.GetStackFrameTags(mapper);
		public string GetStackTag(IStackTagMapper mapper) => this.StackLast?.GetStackTag(mapper);
		public string GetStackTagPath(IStackTagMapper mapper) => this.StackLast?.GetStackTagPath(mapper);
		public bool IsIdle => this.StackLast?.IsIdle ?? false;
		public IReadOnlyList<StackFrame> Frames => this.StackLast?.Frames;
	}


	/*
		Required for rendering a hierarchical stack column.
	*/
	public class StackSnapshotAccessProvider : IStackSnapshotAccessProvider
	{
		readonly SymLoadProgress symLoadProgress;

		public StackSnapshotAccessProvider(SymLoadProgress _symLoadProgress) { this.symLoadProgress = _symLoadProgress; }

		public int SymLoadProgress => symLoadProgress?.PctProcessed ?? 0; // 0 = disabled

		public bool HasUniqueStart => false;

		// This is what appears in an otherwise empty column.
		public string PastEndValue => strNA; // "N/A"

		protected static bool IsEmpty(MyStackSnapshot stack)
		{
			if (CountAll(stack) == 0) return true;
			return Array.TrueForAll(stack.rgStack, s => s?.Frames == null);
		}

		public bool IsNull(IStackSnapshot stack) => IsEmpty((MyStackSnapshot)stack);

		protected static int CountAll(MyStackSnapshot stack) => stack?.rgStack?.Length ?? 0;

		static int GetFrameCount(IStackSnapshot stack) => stack?.Frames?.Count ?? 0;

		// Return the count of stack frames.
		// For brevity, trim the final ~three ETW stack frames in ntdll.dll
		protected int _GetCount(IStackSnapshot stack)
		{
			// Empirically: EtwEventWrite, EtwpEventWriteFull, ZwTraceEvent
			const int cFramesTrim = 3;

			int cFrames = GetFrameCount(stack);

			int cTrim = 0;
			int iFrameMax = Math.Min(cFrames - cFramesTrim + 1, cFramesTrim + 1);
			for (int iFrame = 0; iFrame < iFrameMax; ++iFrame)
			{
				if (!stack.Frames[iFrame].Image?.FileName?.Equals("ntdll.dll") ?? true)
					break;

				++cTrim;
			}

			if (cTrim == cFramesTrim)
				cFrames -= cTrim;

			return cFrames;
		}

		public string _GetValue(IStackSnapshot stack, in MyStackSnapshot.Attributes attrib, int index)
		{
#if !StackTitle
			if (stack?.Frames != null)
				++index; // title + frames
#endif
			if (index > 0)
				return GetStackFrame(stack, index - 1);

#if StackTitle
			return ThreadTitle(in attrib, stack?.Frames != null);
#else
			return "Missing Stackwalk";
#endif
		}

		// Invoked for Last Stack
		public int GetCount(IStackSnapshot stack)
		{
			MyStackSnapshot myStack = (MyStackSnapshot)stack;

			AssertCritical(myStack?.StackLast == null || Equals(stack, myStack.StackLast));

			if (IsEmpty(myStack)) return 0;

			int count = _GetCount(stack);
#if StackTitle
			++count; // title + frames
#else
			if (count == 0)
				++count; // title only
#endif
			return count;
		}

		// Invoked for Last Stack
		public string GetValue(IStackSnapshot stack, int index)
		{
			MyStackSnapshot myStack = (MyStackSnapshot)stack;

			AssertCritical(myStack?.StackLast == null || Equals(stack, myStack.StackLast));

			if (IsEmpty(myStack)) return PastEndValue;

			return _GetValue(stack, in ((MyStackSnapshot)stack).rgAttrib[^1], index);

		}


		//// vvv Not Invoked vvv
		public int GetHashCode(IStackSnapshot stack) => stack?.Hash() ?? 0;

		public bool Equals(IStackSnapshot x, IStackSnapshot y) => x.ThreadId == y.ThreadId && x.Timestamp.Nanoseconds == y.Timestamp.Nanoseconds;

		public IStackSnapshot GetParent(IStackSnapshot collection) => throw new NotImplementedException();
		//// ^^^ Not Invoked ^^^

		protected string GetStackFrame(IStackSnapshot stack, int index)
		{
			int iFwd = GetFrameCount(stack) - index - 1;
			if (iFwd < 0) { return PastEndValue; }

			const string strUnknown = "?";
			string function = strUnknown;
			string module = null;

			try
			{
				var frame = stack.Frames[iFwd];
				module = frame.Image?.FileName ?? strUnknown;

				if (frame.Symbol != null)
				{
					function = frame.Symbol.FunctionName ?? strUnknown;
				}
				else if (frame.Image != null)
				{
					int pct = this.SymLoadProgress;

					if (pct < 100) // 100%
					{
						StringBuilder builderPct =
							new StringBuilder(module.Length + "!<Resolving 99%> ".Length);

						if (pct != 0)
							builderPct.AppendFormat("{0}!<Resolving {1}%>", module, pct);
						else
							builderPct.AppendFormat("{0}!<Symbols Disabled>", module, pct);

						return builderPct.ToString();
					}
				}
			}
			catch
			{
				AssertImportant(false);
				module ??= strUnknown;
			}

			StringBuilder builder = new StringBuilder(module.Length + function.Length + 2);
			builder.AppendFormat("{0}!{1}", module, function);

			return builder.ToString();
		} // GetStackFrame


		protected static string ThreadTitle(in MyStackSnapshot.Attributes attrib, bool fPresent)
		{
			StringBuilder builder = new StringBuilder(160);

			if (attrib.type == XLinkType.None || attrib.type == XLinkType.Thread)
			{
				AssertImportant(attrib.strSubType == null || attrib.strSubType.Equals("Create")); // else what?
				builder.AppendFormat("> Thread: ");
			}
			else
			{
				builder.AppendFormat("> Pool Thread: {0} {1}, ", attrib.type.ToString(), attrib.strSubType);
			}

			string strShow = fPresent ? "below" : "missing stack";

			if (attrib.tidEnqueue == TID.Unknown)
			{
				if (attrib.tidExec != TID.Unknown)
					builder.AppendFormat("TID Exec ({1}): {0}", attrib.tidExec, strShow);
				else
					builder.AppendFormat(strShow);
			}
			else
			{
				builder.AppendFormat("TID Enqueue ({2}): {0}, TID Exec (next): {1}", attrib.tidEnqueue, attrib.tidExec, strShow);
			}

			if (attrib.cCut > 0)
				builder.AppendFormat(", {0} stack(s) omitted for brevity.", attrib.cCut);

			return builder.ToString();
		}
	} // StackSnapshotAccessProvider


	/*
		Render a MyStackSnapshot for the first stack, with special handling for when it's empty.
	*/
	public class FirstStackSnapshotAccessProvider : StackSnapshotAccessProvider, IStackSnapshotAccessProvider
	{
		public FirstStackSnapshotAccessProvider(SymLoadProgress _symLoadProgress) : base(_symLoadProgress) { }

#if DEBUG
		public new int GetHashCode(IStackSnapshot stack) => throw new NotImplementedException();

		public new bool Equals(IStackSnapshot x, IStackSnapshot y) => throw new NotImplementedException();
#endif // DEBUG

		public new int GetCount(IStackSnapshot stack)
		{
			AssertImportant(stack != null);

			MyStackSnapshot myStack = (MyStackSnapshot)stack;

			if (IsEmpty(myStack)) return 0;

			if (myStack.IsOneStack)
				return 1; // "One Stack >"

			int count = _GetCount(myStack.StackFirst);
#if StackTitle
			++count; // title + frames
#else
			if (count == 0)
				++count; // title only
#endif
			return count;
		}

		public new string GetValue(IStackSnapshot stack, int index)
		{
			MyStackSnapshot myStack = (MyStackSnapshot)stack;

			if (IsEmpty(myStack)) return PastEndValue;

			if (index == 0 && myStack.IsOneStack)
				return "One Stack >";

			return _GetValue(myStack.StackFirst, in myStack.rgAttrib[0], index);
		}
	}


	/*
		Render a MyStackSnapshot (Middle Stack Chain), which is an aggregation of stacks from chained threadpool tasks.
	*/
	public class MiddleStackSnapshotAccessProvider : StackSnapshotAccessProvider, IStackSnapshotAccessProvider
	{
		public MiddleStackSnapshotAccessProvider(SymLoadProgress _symLoadProgress) : base(_symLoadProgress) {}

#if DEBUG
		public new int GetHashCode(IStackSnapshot stack) => throw new NotImplementedException();

		public new bool Equals(IStackSnapshot x, IStackSnapshot y) => throw new NotImplementedException();
#endif // DEBUG

		// The middle stacks exclude the first and last, naturally.
		new static bool IsEmpty(MyStackSnapshot stack) => CountAll(stack) < 2 || StackSnapshotAccessProvider.IsEmpty(stack);

		public new bool IsNull(IStackSnapshot stack) => IsEmpty((MyStackSnapshot)stack);

		public new int GetCount(IStackSnapshot stack)
		{
			AssertImportant(stack != null);

			MyStackSnapshot myStack = (MyStackSnapshot)stack;

			if (IsEmpty(myStack)) return 0;

			int cFrames = 0;
			int iStackMax = myStack.rgStack.Length - 1;
			for (int iStack = 1; iStack < iStackMax; ++iStack)
			{
				// Include a title on each stack.
				cFrames += _GetCount(myStack.rgStack[iStack]) + 1;
			}
			return cFrames;
		}

		public new string GetValue(IStackSnapshot stack, int index)
		{
			AssertImportant(stack != null);

			MyStackSnapshot myStack = (MyStackSnapshot)stack;

			if (IsEmpty(myStack)) return PastEndValue;

			int iStackMax = myStack.rgStack.Length - 1;
			for (int iStack = 1; iStack < iStackMax; ++iStack)
			{
				IStackSnapshot stackM = myStack.rgStack[iStack];
				int cFrames = _GetCount(stackM) + 1; // plus the "thread title" string

				if (index == 0)
					return ThreadTitle(in myStack.rgAttrib[iStack], stackM?.Frames != null);

				if (index < cFrames)
					return GetStackFrame(stackM, index - 1);

				index -= cFrames;
			}

			return PastEndValue;
		} // GetValue
	} // MiddleStackSnapshotAccessProvider


	/*
		Render a MyStackSnapshot (Full Stack Chain), which is an aggregation of stacks from chained threadpool tasks.
	*/
	public class FullStackSnapshotAccessProvider : StackSnapshotAccessProvider, IStackSnapshotAccessProvider
	{
		public FullStackSnapshotAccessProvider(SymLoadProgress _symLoadProgress) : base(_symLoadProgress) {}

#if DEBUG
		public new int GetHashCode(IStackSnapshot stack) => throw new NotImplementedException();

		public new bool Equals(IStackSnapshot x, IStackSnapshot y) => throw new NotImplementedException();
#endif // DEBUG

		public new int GetCount(IStackSnapshot stack)
		{
			AssertImportant(stack != null);

			MyStackSnapshot myStack = (MyStackSnapshot)stack;

			if (IsEmpty(myStack)) return 0;

			int cFrames = 0;
			foreach (IStackSnapshot stackF in myStack.rgStack)
			{
				// Include a title on each stack.
				cFrames += _GetCount(stackF) + 1;
			}

			// Omit the title on the top stack.
			if (myStack.rgStack.Length > 0 && myStack.rgStack[0] != null)
				--cFrames;

			return cFrames;
		}

		public new string GetValue(IStackSnapshot stack, int index)
		{
			AssertImportant(stack != null);

			MyStackSnapshot myStack = (MyStackSnapshot)stack;

			if (IsEmpty(myStack)) return PastEndValue;

			// Omit the title on the top stack.
			if (myStack.rgStack.Length > 0 && myStack.rgStack[0] != null)
				++index;

			int iStack = 0;
			foreach (IStackSnapshot stackF in myStack.rgStack)
			{
				int cFrames = _GetCount(stackF) + 1; // plus the "thread title" string

				if (index == 0)
					return ThreadTitle(in myStack.rgAttrib[iStack], stackF?.Frames != null);

				if (index < cFrames)
					return GetStackFrame(stackF, index - 1);

				index -= cFrames;
				++iStack;
			}
			return PastEndValue;
		} // GetValue
	} // FullStackSnapshotAccessProvider
} // namespace NetBlameCustomDataSource.Stack
