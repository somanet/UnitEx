using McMaster.Extensions.CommandLineUtils;

using System;
using System.IO;

namespace Unitex
{
	static class Program
	{
		static readonly System.Reflection.Assembly ExecutingAssembly = System.Reflection.Assembly.GetExecutingAssembly();

		static void Main(string[] args)
		{
			var app = new CommandLineApplication(false);

			app.FullName = nameof(Unitex);
			app.Name = Path.GetFileName(ExecutingAssembly.Location);

			var argExecutable = app.Option("-e", "Executable to inject into.", CommandOptionType.SingleValue);
			var argOutput = app.Option("-o", "Output file.", CommandOptionType.SingleValue);
			var argAdd = app.Option("-a", "Another files to add.", CommandOptionType.MultipleValue);
			var argPreExtract = app.Option("-p", "Pre-extract files.", CommandOptionType.MultipleValue);
			var argCompress = app.Option("-c", "Compress files.", CommandOptionType.NoValue);

			app.Execute(args);

			if (!argExecutable.HasValue() || !argOutput.HasValue())
			{
				app.ShowHelp();
				ErrorExit();
			}

			var executablePath = Path.GetFullPath(argExecutable.Value());

			if (!File.Exists(executablePath))
				ErrorExit($"File '{executablePath}' does not exists.");

			var options = new MergeOptions()
			{
				Executable = executablePath, //Path.GetFullPath(argExecutable.Value()),
				Output = Path.GetFullPath(argOutput.Value()),
				DoCompression = argCompress.HasValue(),
				FilesToAdd = argAdd.Values,
				PreExtractDlls = argPreExtract.Values
			};


			try
			{
				Console.WriteLine("Loading...");

                var dllMerger = new DllMerger(options);

                dllMerger.DoMerge();

				Console.WriteLine("Done...");
			}
			catch (Exception ex)
			{
				ErrorExit(ex.ToString());
			}
		}

		static void ErrorExit(string message = null)
		{
			if (message != null)
				Console.Error.WriteLine(message);

			Environment.Exit(1);
		}

	}
}
