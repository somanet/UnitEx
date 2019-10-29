using Mono.Cecil;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Unitex
{
	public class MergeItem
	{
		public string DllPath;
		public string ResourceName;

		public MergeItem(string dllPath, string resourceName)
		{
			DllPath = dllPath;
			ResourceName = resourceName;
		}
	}


	public class MergeOptions
	{
		public string Executable;
		public string Output;
		public bool DoCompression;
		public IEnumerable<string> FilesToAdd;
		public IEnumerable<string> PreExtractDlls;
	}


	public class DllMerger
	{
		private AssemblyDefinition _assembly;
		private MergeOptions _options;
		private IEnumerable<MergeItem> _dllItemsToMerge = new List<MergeItem>();

		public DllMerger(MergeOptions options)
		{
            var assemblyResolver = new DefaultAssemblyResolver();
            assemblyResolver.AddSearchDirectory(Path.GetDirectoryName(options.Executable));
            var assembly = AssemblyDefinition.ReadAssembly(options.Executable, new ReaderParameters() { AssemblyResolver = assemblyResolver });

			_assembly = assembly;
			_options = options;
		}


		public void DoMerge()
		{

			var dllPathes = GetDllPathes(Path.GetDirectoryName(_options.Executable), _options.FilesToAdd);

			// dll을 삽입
			Console.WriteLine("Embedding:");
			var embedDllsResult = EmbedDlls(dllPathes, _options.DoCompression);

			Console.WriteLine("Writing compression...");
			WriteCompression(embedDllsResult, _options.DoCompression);

			Console.WriteLine("Writing pre-extraction...");
			WritePreExtract(embedDllsResult, _options.PreExtractDlls);




			Console.WriteLine("Injecting DllLoader:");
			InjectDllLoader();

			Console.WriteLine("Writing...");
			WriteAssembly(_options.Output);
		}

		/// <summary>
		/// 하나로 합칠 dll들의 경로의 배열을 리턴합니다.
		/// </summary>
		/// <param name="targetDir">dll이 있는 폴더</param>
		/// <param name="dllFilesToAdd">추가적으로 합치고 싶은 dll 파일들의 경로</param>
		private IEnumerable<string> GetDllPathes(string targetDir, IEnumerable<string> dllFilesToAdd)
		{
			var dllFiles = Directory.EnumerateFiles(targetDir, "*.dll", SearchOption.TopDirectoryOnly);
			var dllFilesAdditionally = dllFilesToAdd.Select(Path.GetFullPath).Where(File.Exists);

			return dllFiles.Concat(dllFilesAdditionally);
		}


		private IEnumerable<MergeItem> EmbedDlls(IEnumerable<string> dllPathes, bool compress)
		{
			var result = new List<MergeItem>();

			foreach (var dllPath in dllPathes)
			{
				var resourceName = EmbedDll(_assembly, dllPath, compress);
				result.Add(new MergeItem(dllPath, resourceName));
			}
			return result;
		}

		private string EmbedDll(AssemblyDefinition assembly, string dllPath, bool compress)
		{
			Console.WriteLine($"  {dllPath}");
			var name = Path.GetFileName(dllPath);
			var resourceName = $"{Definitions.PrefixDll}{name}";
			var data = File.ReadAllBytes(dllPath);

            // 압축할 경우
			if (compress)
			{
				using (var s = new MemoryStream())
				{
					using (var deflate = new DeflateStream(s, CompressionLevel.Optimal, true))
					{
						deflate.Write(data, 0, data.Length);
					}
					data = s.ToArray();
				}
			}

			var resource = new EmbeddedResource(resourceName, ManifestResourceAttributes.Private, data);
			assembly.MainModule.Resources.Add(resource);

			return resourceName;
		}

		private void WriteCompression(IEnumerable<MergeItem> embeddedDlls, bool compress)
		{
			var data = compress
				? Encoding.UTF8.GetBytes(string.Join(Definitions.CompressionSeparator.ToString(), embeddedDlls.Select(x => x.ResourceName)))
				: Array.Empty<byte>();

			var resource = new EmbeddedResource(Definitions.CompressionResourceName, ManifestResourceAttributes.Private, data);
			_assembly.MainModule.Resources.Add(resource);
		}

        /// <summary>
		/// 프로그램 실행시 exe파일로 부터 dll형태로 돌려 놓을 파일을 지정하고 리소스로 저장. 이미 embed할 dll경로 중에서
		/// 선택해야 하는 것으로 보임.
		/// </summary>
		/// <param name="embeddedDlls"></param>
		/// <param name="preExtractDlls"></param>
		private void WritePreExtract(IEnumerable<MergeItem> embeddedDlls, IEnumerable<string> preExtractDlls)
		{
			var preExtract = new HashSet<string>(preExtractDlls.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

			var dlls = embeddedDlls
				.Where(x => preExtract.Contains(x.DllPath) || !DllHelper.IsDotNetDll(x.DllPath))
				.Select(x => x.ResourceName);

			var data = Encoding.UTF8.GetBytes(string.Join(Definitions.PreExtractSeparator.ToString(), dlls));

			var resource = new EmbeddedResource(Definitions.PreExtractResourceName, ManifestResourceAttributes.Private, data);
			_assembly.MainModule.Resources.Add(resource);
		}

		private void InjectDllLoader()
		{
			var entryPointClass = _assembly.EntryPoint.DeclaringType;
			var currentExecutingAssembly = System.Reflection.Assembly.GetExecutingAssembly();
			var sourceAssembly = AssemblyDefinition.ReadAssembly(currentExecutingAssembly.Location);

            // InjectMe class를 target 실행파일에 복사해 넣음
			Console.WriteLine($"  methods");
			DllLoaderInjector.Copy(sourceAssembly.MainModule.Types.First(t => t.Name == nameof(DllLoader)), entryPointClass, Definitions.Prefix);

			Console.WriteLine($"  .cctor");
			CctorProcessor.ProcessCctor(entryPointClass, Definitions.Prefix);
		}


		private void WriteAssembly(string output)
		{
			var dir = Path.GetDirectoryName(output);

			if (!Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			_assembly.Write(output);
		}
	}

    static class DllHelper
    {
		public static bool IsDotNetDll(string path)
		{
			try
			{
				AssemblyDefinition.ReadAssembly(path);
				return true;
			}
			catch (BadImageFormatException)
			{
				return false;
			}
		}

	}
}
