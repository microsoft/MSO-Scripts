// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;

using Microsoft.Performance.SDK; // Guard
using Microsoft.Performance.SDK.Processing;


namespace NetBlameCustomDataSource
{
	// In order for a CustomDataSource to be recognized, it MUST satisfy the following:
	//  a) Be a public type
	//  b) Have a public parameterless constructor
	//  c) Implement the ICustomDataSource interface
	//  d) Be decorated with the CustomDataSource[Attribute] attribute
	//  e) Be decorated with at least one of the derivatives of the DataSource[Attribute] attribute

	[ProcessingSource(
		"{67AE371A-9D97-4584-BCC1-F62B4D14737A}",  // The GUID must be unique for your Custom Data Source.
		"Office_NetBlame",                         // The Custom Data Source MUST have a name.
		"Data source for the NetBlame Tool")]      // The Custom Data Source MUST have a description.
	[FileDataSource(
		".etl",                                    // A file extension is REQUIRED.
		"Event Trace Log")]                        // A description is OPTIONAL. The description is what appears in the file open menu to help users understand what the file type actually is.

	public class NetBlameDataSource : ProcessingSource
	{
		public override ProcessingSourceInfo GetAboutInfo()
		{
			return new ProcessingSourceInfo()
			{
				Owners = new ContactInfo[]
				{
					new ContactInfo { Name="Raymond Fowkes", EmailAddresses = new string [] { "rayfo@microsoft.com" } },
					new ContactInfo { Name="\rOffice Fundamentals Performance", EmailAddresses = new string [] { "odevperf@microsoft.com" } }
				},
				ProjectInfo = new ProjectInfo() { Uri = "https://office.visualstudio.com/OE/_git/TWCPerf-Scratch?path=%2FTools%2FWPA-AddIns%2FNetBlame" },
				CopyrightNotice = $"Copyright (C) 2020 Microsoft Corporation. All Rights Reserved.",
				LicenseInfo = null,
				AdditionalInformation = new string[] { GeoLocation.Attribution }
			};
		}

		protected override ICustomDataProcessor CreateProcessorCore(
			IEnumerable<IDataSource> files,
			IProcessorEnvironment processorEnvironment,
			ProcessorOptions options)
		{
			Guard.NotNull(files, nameof(files));
			IDataSource[] allFiles = files.ToArray();
			Guard.IsTrue(allFiles.Length == 1);

			return new NetBlameDataProcessor(
				allFiles[0].Uri.LocalPath,
				options,
				this.ApplicationEnvironment,
				processorEnvironment);
		}

		// Already limited to ETL via: [FileDataSource()]
		protected override bool IsDataSourceSupportedCore(IDataSource ids) => true;
	}

} // NetBlameCustomDataSource
