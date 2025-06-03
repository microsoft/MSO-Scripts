// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Microsoft.Windows.EventTracing; // ClassicEvent

using NetBlameCustomDataSource.Thread;
using NetBlameCustomDataSource.Thread.Classic;
using NetBlameCustomDataSource.WThreadPool;
using NetBlameCustomDataSource.WThreadPool.Classic;

using static NetBlameCustomDataSource.Util;

using Addr32 = System.UInt32;
using Addr64 = System.UInt64;


namespace NetBlameCustomDataSource.Classic
{
	/*
		Parse classic events as they are pre-processed via: traceProcessor.Process()
	*/
	class ClassicEventConsumer : IFilteredEventConsumer
	{
		public WThreadPoolEventConsumer wtpEventConsumer;
		public ThreadEventConsumer threadEventConsumer;

		public IReadOnlyList<Guid> ProviderIds { get; }

		public ClassicEventConsumer(Guid[] rgGuid)
		{
			this.ProviderIds = rgGuid;

			wtpEventConsumer = new WThreadPoolEventConsumer();
			threadEventConsumer = new ThreadEventConsumer();
		}

		public void Process(EventContext ectx)
		{
			AssertCritical(ectx.Event.IsClassic);

			var evtClassic = ectx.Event.AsClassicEvent;

			if (!evtClassic.HasValue) return;
			if (evtClassic.Data.IsEmpty) return;

			if (evtClassic.ProviderId == WThreadPoolTable.guid) // Windows-ThreadPool
			{
				if (evtClassic.Is32Bit)
					wtpEventConsumer.WThreadPoolEvent<Addr32>(in evtClassic);
				else
					wtpEventConsumer.WThreadPoolEvent<Addr64>(in evtClassic);
			}
			else if (evtClassic.ProviderId == ThreadTable.guid) // Thread
			{
				threadEventConsumer.Process(in evtClassic);
			}
			else
			{
				AssertCritical(false);
			}
		} // Process
	} // ClassicEventConsumer
} // NetBlameCustomDataSource.Classic