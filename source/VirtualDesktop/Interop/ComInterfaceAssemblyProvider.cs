﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using WindowsDesktop.Properties;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace WindowsDesktop.Interop
{
	internal class ComInterfaceAssemblyProvider
	{
		private const string _placeholderGuid = "00000000-0000-0000-0000-000000000000";
		private const string _assemblyName = "VirtualDesktop.{0}.generated.dll";

		private static readonly Regex _assemblyRegex = new Regex(@"VirtualDesktop\.(?<build>\d{5}?)(\.\w*|)\.dll");
		private static readonly string _defaultAssemblyDirectoryPath = Path.Combine(ProductInfo.LocalAppData.FullName, "assemblies");
		private static readonly Version _requireVersion = new Version("1.0");
		private static readonly int[] _interfaceVersions = new[] { 10240, 20231, 21313, 22449 };

		private readonly string _assemblyDirectoryPath;
		
		public ComInterfaceAssemblyProvider(string assemblyDirectoryPath)
		{
			this._assemblyDirectoryPath = assemblyDirectoryPath ?? _defaultAssemblyDirectoryPath;
		}

		public Assembly GetAssembly()
		{
			var assembly = this.GetExistingAssembly();
			if (assembly != null) return assembly;

			return this.CreateAssembly();
		}

		private Assembly GetExistingAssembly()
		{
			var searchTargets = new[]
			{
				this._assemblyDirectoryPath,
				Environment.CurrentDirectory,
				Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
				_defaultAssemblyDirectoryPath,
			};

			foreach (var searchPath in searchTargets)
			{
				var dir = new DirectoryInfo(searchPath);
				if (!dir.Exists) continue;

				foreach (var file in dir.GetFiles())
				{
					if (int.TryParse(_assemblyRegex.Match(file.Name).Groups["build"]?.ToString(), out var build)
						&& build == ProductInfo.OSBuild)
					{
						try
						{
							var name = AssemblyName.GetAssemblyName(file.FullName);
							if (name.Version >= _requireVersion)
							{
								System.Diagnostics.Debug.WriteLine($"Assembly found: {file.FullName}");
#if !DEBUG
								return Assembly.LoadFile(file.FullName);
#endif
							}
						}
						catch (BadImageFormatException) { }
					}
				}
			}

			return null;
		}

		private Assembly CreateAssembly()
		{
			var executingAssembly = Assembly.GetExecutingAssembly();
			var interfaceVersion = _interfaceVersions
				.Reverse()
				.First(build => build <= ProductInfo.OSBuild);
			var interfaceNames = executingAssembly
				.GetTypes()
				.SelectMany(x => x.GetComInterfaceNamesIfWrapper(interfaceVersion))
				.Where(x => x != null)
				.ToArray();
			var iids = IID.GetIIDs(interfaceNames);
			var compileTargets = new List<string>();
			{
				var assemblyInfo = executingAssembly.GetManifestResourceNames().Single(x => x.Contains("AssemblyInfo"));
				var stream = executingAssembly.GetManifestResourceStream(assemblyInfo);
				if (stream != null)
				{
					using (var reader = new StreamReader(stream, Encoding.UTF8))
					{
						var sourceCode = reader.ReadToEnd()
							.Replace("{VERSION}", ProductInfo.OSBuild.ToString())
							.Replace("{BUILD}", interfaceVersion.ToString());
						compileTargets.Add(sourceCode);
					}
				}
			}

			foreach (var name in executingAssembly.GetManifestResourceNames())
			{
				var texts = Path.GetFileNameWithoutExtension(name)?.Split('.');
				var typeName = texts.LastOrDefault();
				if (typeName == null) continue;

				if (int.TryParse(string.Concat(texts[texts.Length - 2].Skip(1)), out var build) && build != interfaceVersion) continue;

				var interfaceName = interfaceNames.FirstOrDefault(x => typeName == x);
				if (interfaceName == null) continue;
				if (!iids.ContainsKey(interfaceName)) continue;

				var stream = executingAssembly.GetManifestResourceStream(name);
				if (stream == null) continue;

				using (var reader = new StreamReader(stream, Encoding.UTF8))
				{
					var sourceCode = reader.ReadToEnd().Replace(_placeholderGuid, iids[interfaceName].ToString());
					compileTargets.Add(sourceCode);
				}
			}

			return this.Compile(compileTargets.ToArray());
		}

		private Assembly Compile(IEnumerable<string> sources)
		{
			var dir = new DirectoryInfo(this._assemblyDirectoryPath);
			if (dir.Exists == false) dir.Create();

			var path = Path.Combine(dir.FullName, string.Format(_assemblyName, ProductInfo.OSBuild));
			var syntaxTrees = sources.Select(x => SyntaxFactory.ParseSyntaxTree(x));
			var references = AppDomain.CurrentDomain.GetAssemblies()
				.Concat(new[] { Assembly.GetExecutingAssembly(), })
				.Select(x => x.Location)
				.Select(x => MetadataReference.CreateFromFile(x));
			var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
			var compilation = CSharpCompilation.Create(_assemblyName)
				.WithOptions(options)
				.WithReferences(references)
				.AddSyntaxTrees(syntaxTrees);

			var result = compilation.Emit(path);
			if (result.Success)
			{
				return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
			}

			File.Delete(path);

			var nl = Environment.NewLine;
			var message = $"Failed to compile COM interfaces assembly.{nl}{string.Join(nl, result.Diagnostics.Select(x => $"  {x.GetMessage()}"))}";

			throw new Exception(message);
		}
	}
}
