using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Unitex
{
	static class DllLoader
	{
		private static readonly object _lockObject;

		private static readonly Assembly _executingAssembly;
		private static readonly string[] _compressedResources;

		// Dll 로딩 작업은 실행파일의 MainEntry(일반적으로 Main() )에 도달하기 전에
		// 완료되어야 하므로 static constructor에서 처리해주는 것이 합리적임.
		static DllLoader()
		{
			_lockObject = new object();

			// 어셈블리를 찾으려고 했는데 찾지 못한경우 이벤트 발생
			AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolve;

			_executingAssembly = Assembly.GetExecutingAssembly();

			// 이전에 압축해서 EmbededResource에 저장해두었던 압축한 dll의 이름을 가져옴.
			_compressedResources = GetCompressionNames(_executingAssembly);

			PreExtractDlls();
		}

		// 로드하려는 어셈블리를 찾지 못했을때 발생하는 이벤트.
		// 이벤트 콜백내부에서 필요한 어셈블리를 리턴값으로 호출한 메소드에게 전달한다.
		// 임의로 어셈블리를 바꾸어 전달하는 것도 가능하다.
		static Assembly AssemblyResolve(object sender, ResolveEventArgs args)
		{
			lock (_lockObject)
			{
				Log($"Resolving '{args.Name}'.");

				var assemblyName = new AssemblyName(args.Name);

				// 현재 로드되어 있는 어셈블리중에서 찾아서 Assembly를 리턴하거나 ( GetLoadedAssembly() ),
				// 로드되어 있지 않은 경우 Embedded된 어셈블리중에서 찾아서 Assembly를 리턴( GetEmbeddedAssembly() )
				return GetLoadedAssembly(assemblyName) ?? GetEmbeddedAssembly(assemblyName);
			}
		}

		static void PreExtractDlls()
		{
			var executingDirectory = Path.GetDirectoryName(_executingAssembly.Location);

			// dll이 Injected된 어셈블리로부터 pre-extract names를 가져온뒤 파일의 형태로 복원한다. 
			foreach (var name in GetPreExtractNames(_executingAssembly))
			{
				var dllName = name.Remove(0, Definitions.PrefixDll.Length);
				Log($"Pre-extracting '{dllName}'.");

				using (var s = GetResourceDllStream(name))
				{
					var path = Path.Combine(executingDirectory, dllName);
					try
					{
						if (File.Exists(path) && OnDiskSameAsInResource(s, path))
						{
							continue;
						}
						SaveToDisk(s, path);
					}
					catch (IOException ex)
					{
						throw new ApplicationException($"Unable to pre-extract DLL '{dllName}'.", ex);
					}

				}
			}
		}

		static Assembly GetLoadedAssembly(AssemblyName assemblyName)
		{
			Log($"Searching for '{assemblyName.Name}' in loaded assemblies.");
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (assembly.FullName == assemblyName.FullName || assembly.GetName().Name == assemblyName.Name)
				{
					Log($"Found '{assemblyName.Name}' in loaded assemblies.");
					return assembly;
				}
			}
			return null;
		}

		static Assembly GetEmbeddedAssembly(AssemblyName assemblyName)
		{
			Log($"Searching for '{assemblyName.Name}' in embedded assemblies.");

			var name = $"{Definitions.PrefixDll}{assemblyName.Name}.dll";

			// 저장되어 있던 dll을 가져와서 assembly를 load
			using (var s = GetResourceDllStream(name))
			{
				if (s != null)
				{
					Log($"Found '{assemblyName.Name}' in embedded assemblies.");
					return Assembly.Load(ReadAllBytes(s));
				}
			}
			return null;
		}

		static bool OnDiskSameAsInResource(Stream resource, string path)
		{
			resource.Position = 0;
			using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
			{
				if (fs.Length != resource.Length)
					return false;
				using (BinaryReader resourceReader = new BinaryReader(resource, Encoding.UTF8, true),
					fileReader = new BinaryReader(fs, Encoding.UTF8, true))
				{
					var fileData = fileReader.ReadBytes((int)fs.Length);
					var resourceData = resourceReader.ReadBytes((int)resource.Length);
					if (!fileData.SequenceEqual(resourceData))
						return false;
				}
			}
			return true;
		}

		static void SaveToDisk(Stream resource, string path)
		{
			resource.Position = 0;
			using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write))
			{
				resource.CopyTo(fs);
				fs.SetLength(resource.Length);
			}
		}

		static byte[] ReadAllBytes(Stream resource)
		{
			resource.Position = 0;
			using (var reader = new BinaryReader(resource, Encoding.UTF8, true))
			{
				return reader.ReadBytes((int)resource.Length);
			}
		}

		static Stream GetResourceDllStream(string name)
		{
			var manifest = _executingAssembly.GetManifestResourceStream(name);
			if (manifest == null)
				return null;

			// 압축이 되어있지 않은 경우
			if (!_compressedResources.Contains(name))
				return manifest;

			// 압축이 되어 있는 경우 압축해제후 Stream 리턴
			var memory = new MemoryStream();
			using (var deflate = new DeflateStream(manifest, CompressionMode.Decompress, true))
			{
				deflate.CopyTo(memory);
			}
			return memory;
		}

		static string[] GetCompressionNames(Assembly executingAssembly)
		{

			using (var s = executingAssembly.GetManifestResourceStream(Definitions.CompressionResourceName))
			{
				var data = Encoding.UTF8.GetString(ReadAllBytes(s));

				if (data == string.Empty)
					return Array.Empty<string>();

				return data.Split(Definitions.PreExtractSeparator);
			}
		}

		// Inject된 파일에 Pre-Extraction을 위하여 명시해둔 내용을 리턴한다. 
		static string[] GetPreExtractNames(Assembly executingAssembly)
		{
			using (var s = executingAssembly.GetManifestResourceStream(Definitions.PreExtractResourceName))
			{
				var data = Encoding.UTF8.GetString(ReadAllBytes(s));

				if (data == string.Empty)
					return Array.Empty<string>();

				return data.Split(Definitions.PreExtractSeparator);
			}
		}

		static void Log(string message)
		{
			if (int.TryParse(Environment.GetEnvironmentVariable(Definitions.LoggingEnvironmentVariable), out var logging) && logging == 1)
			{
				File.AppendAllLines(Definitions.LogFile, new[] { message });
			}
		}
	}
}
