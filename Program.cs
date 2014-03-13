using Microsoft.Cci;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.GraphModel;
using Microsoft.VisualStudio.GraphModel.Styles;

namespace ildepends
{
    class Program
    {
        static GraphCategory UnresolvedCategory;
        static GraphCategory AssemblyCategory;
        static GraphNodeIdName AssemblyName = GraphNodeIdName.Get("Assembly", "Assembly", typeof(Uri));


        List<string> _directories = new List<string>();
        List<string> _paths = new List<string>();
        Dictionary<string, string> _files = new Dictionary<string, string>();
        ConsoleHostEnvironment _host;
        bool excludeDotNet;
        string output = "out.dgml";
        Dictionary<string, Uri> frameworkAssemblies = new Dictionary<string, Uri>();

        static void PrintUsage()
        {
            Console.WriteLine("Usage: ildepends [options] assemblies...");
            Console.WriteLine("Generates a dependency graph of all the assemblies referenced by the given input assemblies");
            Console.WriteLine("For example:");
            Console.WriteLine(@"    ildepends c:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Xml.Linq.dll");
            Console.WriteLine();
            Console.WriteLine("Produces a file named out.dgml which you can then view in Visual Studio");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -s path     specifies a search path for resolving assemblies");
            Console.WriteLine("  -o path     specifies the output DGML file name, default 'out.dgml'");
            Console.WriteLine("  -e          exclude any .NET framework assemblies from the graph");
            Console.WriteLine("  -d          specifies the target directories");
        }

        static void Main(string[] args)
        {
            Program p = new Program();
            if (!p.ParseCommandLine(args))
            {
                PrintUsage();
                return;
            }

            p.Process();
        }

        bool ParseCommandLine(string[] args)
        {

            for (int i = 0, n = args.Length; i < n; i++)
            {
                string arg = args[i];
                if (arg[0] == '-' || arg[0] == '/')
                {
                    switch (arg.Substring(1).ToLowerInvariant())
                    {
                        case "h":
                        case "help":
                        case "?":
                            return false;
                        case "s":
                            if (i + 1 < n)
                            {
                                _paths.Add(args[++i]);
                            }
                            break;
                        case "o":
                            if (i + 1 < n)
                            {
                                output = args[++i];
                            }
                            break;
                        case "e":
                            excludeDotNet = true;
                            break;

                        case "d":
                            if (i + 1 < n)
                            {
                                _directories.Add(args[++i]);
                            }
                            break;
                        default:
                            return false;
                    }
                }
                else
                {
                    _files.Add(Path.GetFileName(arg), arg);
                }
            }
            return _files.Count > 0 || _directories.Count > 0;
        }

        void Process()
        {

            GraphSchema assemblySchema = new GraphSchema("ildependsSchema");
            UnresolvedCategory = assemblySchema.Categories.AddNewCategory("Unresolved");
            AssemblyCategory = assemblySchema.Categories.AddNewCategory("CodeSchema_Assembly");

            string windir = Environment.GetEnvironmentVariable("WINDIR");
            _paths.Add(windir + @"\Microsoft.NET\Framework\v4.0.30319");

            _host = new ConsoleHostEnvironment(_paths.ToArray(), true);

            Graph graph = new Graph();
            graph.AddSchema(assemblySchema);

            GraphConditionalStyle errorStyle = new GraphConditionalStyle(graph);
            GraphCondition condition = new GraphCondition(errorStyle);
            condition.Expression = "HasCategory('Unresolved')";
            GraphSetter setter = new GraphSetter(errorStyle, "Icon");
            setter.Value = "pack://application:,,,/Microsoft.VisualStudio.Progression.GraphControl;component/Icons/kpi_red_sym2_large.png";
            graph.Styles.Add(errorStyle);

            foreach (var dir in _directories)
            {
                foreach (var target in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories))
                {
                    _files.Add(Path.GetFileName(target), target);
                }
            }

            foreach (String fileName in this._files.Values)
            {
                String fullPath = Path.GetFullPath(fileName);
                try
                {
                    Console.WriteLine("Processing: " + fullPath);
                    IAssembly assembly = _host.LoadUnitFrom(fileName) as IAssembly;
                    GraphNode root = graph.Nodes.GetOrCreate(GetNodeID(assembly.AssemblyIdentity), assembly.AssemblyIdentity.Name.Value, AssemblyCategory);
                    WalkDependencies(graph, root, assembly, new HashSet<IAssembly>());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("#Error: " + ex.Message);
                }
            }

            graph.Save(output);
        }

        Uri FindAssembly(AssemblyIdentity id)
        {
            string name = id.Name.Value; ;

            if (!string.IsNullOrEmpty(id.Location))
            {
                Uri baseUri = new Uri(Directory.GetCurrentDirectory() + "\\");
                return new Uri(baseUri, id.Location);
            }

            if (frameworkAssemblies.Count == 0)
            {
                // populate it.
                foreach (string path in _host.SearchPaths)
                {
                    FindAssemblies(path);
                }
            }

            Uri result = null;
            if (frameworkAssemblies.TryGetValue(name.ToLowerInvariant(), out result))
            {
                return result;
            }
            else
            {
                /// hmmm... make one up then...
                string targetFilePath;
                if (_files.TryGetValue(name + ".dll", out targetFilePath) && !string.IsNullOrEmpty(targetFilePath))
                {
                    Uri baseUri = new Uri(Directory.GetCurrentDirectory() + "\\");
                    result = new Uri(baseUri, targetFilePath);
                }
                else
                {
                    result = new Uri(Directory.GetCurrentDirectory() + "\\" + name + ".dll");
                }
            }

            return result;
        }

        bool IsFrameworkAssembly(AssemblyIdentity id)
        {
            string key = id.Name.Value.ToLowerInvariant();
            return frameworkAssemblies.ContainsKey(key);
        }

        private void FindAssemblies(string path)
        {
            foreach (string file in Directory.GetFiles(path, "*.dll"))
            {
                frameworkAssemblies.Add(Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), new Uri(file));
            }
            foreach (string dir in Directory.GetDirectories(path))
            {
                FindAssemblies(dir);
            }
        }

        GraphNodeId GetNodeID(AssemblyIdentity id)
        {
            return GraphNodeId.GetNested(new GraphNodeId[] {
                GraphNodeId.GetPartial(AssemblyName, FindAssembly(id)) });
        }

        private void WalkDependencies(Graph graph, GraphNode root, IAssembly assembly, HashSet<IAssembly> visited)
        {
            visited.Add(assembly);
            foreach (IAssemblyReference r in assembly.AssemblyReferences)
            {
                if (excludeDotNet && IsFrameworkAssembly(r.AssemblyIdentity))
                {
                    continue;
                }

                GraphNode node = graph.Nodes.GetOrCreate(GetNodeID(r.AssemblyIdentity), r.AssemblyIdentity.Name.Value, AssemblyCategory);
                graph.Links.GetOrCreate(root, node);

                IAssembly referenced = r.ResolvedAssembly;
                if (referenced != null)
                {
                    if (!visited.Contains(referenced))
                    {
                        WalkDependencies(graph, node, referenced, visited);
                    }
                }
                else
                {
                    node.AddCategory(UnresolvedCategory);
                }
            }
        }
    }


    /// <summary>
    /// A host that is a subtype of Microsoft.Cci.PeReader.DefaultHost that also maintains
    /// a (mutable) table mapping assembly names to paths.
    /// When an assembly is to be loaded, if its name is in the table, then the
    /// associated path is used to load it.
    /// </summary>
    public class FullyResolvedPathHost : Microsoft.Cci.PeReader.DefaultHost
    {

        private Dictionary<string, string> assemblyNameToPath = new Dictionary<string, string>();

        /// <summary>
        /// Adds a new pair of (assembly name, path) to the table of candidates to use
        /// when searching for a unit to load. Overwrites previous entry if the assembly
        /// name is already in the table. Note that "assembly name" does not have an
        /// extension.
        /// </summary>
        /// <param name="path">
        /// A valid path in the file system that ends with a file name. The
        /// file name (without extension) is used as the key in the candidate
        /// table.
        /// </param>
        /// <returns>
        /// Returns true iff <paramref name="path"/> is a valid path pointing
        /// to an existing file and the table was successfully updated.
        /// </returns>
        public virtual bool AddResolvedPath(string path)
        {
            if (path == null) return false;
            if (!File.Exists(path)) return false;
            var fileNameWithExtension = Path.GetFileName(path);
            if (String.IsNullOrEmpty(fileNameWithExtension)) return false;
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (this.assemblyNameToPath.ContainsKey(fileName))
                this.assemblyNameToPath[fileName] = path;
            else
                this.assemblyNameToPath.Add(fileName, path);
            return true;
        }

        /// <summary>
        /// Returns the unit that is stored at the given location, or a dummy unit if no unit exists at that location or if the unit at that location is not accessible.
        /// </summary>
        /// <param name="location">A path to the file that contains the unit of metdata to load.</param>
        /// <returns></returns>
        public override IUnit LoadUnitFrom(string location)
        {
            string pathFromTable;
            var assemblyName = Path.GetFileNameWithoutExtension(location);
            if (this.assemblyNameToPath.TryGetValue(assemblyName, out pathFromTable))
            {
                return base.LoadUnitFrom(pathFromTable);
            }
            else
            {
                return base.LoadUnitFrom(location);
            }
        }

        /// <summary>
        /// Given the identity of a referenced assembly (but not its location), apply host specific policies for finding the location
        /// of the referenced assembly.
        /// Returns an assembly identity that matches the given referenced assembly identity, but which includes a location.
        /// If the probe failed to find the location of the referenced assembly, the location will be "unknown://location".
        /// </summary>
        /// <param name="referringUnit">The unit that is referencing the assembly. It will have been loaded from somewhere and thus
        /// has a known location, which will typically be probed for the referenced assembly.</param>
        /// <param name="referencedAssembly">The assembly being referenced. This will not have a location since there is no point in probing
        /// for the location of an assembly when you already know its location.</param>
        /// <returns>
        /// An assembly identity that matches the given referenced assembly identity, but which includes a location.
        /// If the probe failed to find the location of the referenced assembly, the location will be "unknown://location".
        /// </returns>
        public override AssemblyIdentity ProbeAssemblyReference(IUnit referringUnit, AssemblyIdentity referencedAssembly)
        {
            string pathFromTable;
            var assemblyName = referencedAssembly.Name.Value;
            if (this.assemblyNameToPath.TryGetValue(assemblyName, out pathFromTable))
            {
                return new AssemblyIdentity(referencedAssembly, pathFromTable);
            }
            else
            {
                return base.ProbeAssemblyReference(referringUnit, referencedAssembly);
            }
        }


    }

    internal class ConsoleHostEnvironment : FullyResolvedPathHost
    {

        internal readonly Microsoft.Cci.Immutable.PlatformType platformType;

        internal ConsoleHostEnvironment(string[] libPaths, bool searchGAC)
            : base()
        {
            foreach (var p in libPaths)
            {
                this.AddLibPath(p);
            }
            this.SearchInGAC = searchGAC;
            this.platformType = new Microsoft.Cci.Immutable.PlatformType(this);
        }

        public override IUnit LoadUnitFrom(string location)
        {
            IUnit result = this.peReader.OpenModule(BinaryDocument.GetBinaryDocumentForFile(location, this));
            this.RegisterAsLatest(result);
            return result;
        }

        public List<string> SearchPaths
        {
            get { return this.LibPaths; }
        }

        protected override IPlatformType GetPlatformType()
        {
            return this.platformType;
        }

        /// <summary>
        /// override this here to not use memory mapped files since we want to use asmmeta in msbuild and it is sticky
        /// </summary>
        public override IBinaryDocumentMemoryBlock/*?*/ OpenBinaryDocument(IBinaryDocument sourceDocument)
        {
            try
            {
                IBinaryDocumentMemoryBlock binDocMemoryBlock = UnmanagedBinaryMemoryBlock.CreateUnmanagedBinaryMemoryBlock(sourceDocument.Location, sourceDocument);
                this.disposableObjectAllocatedByThisHost.Add((IDisposable)binDocMemoryBlock);
                return binDocMemoryBlock;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
