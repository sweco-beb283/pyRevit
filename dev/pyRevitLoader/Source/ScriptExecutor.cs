using System;
using System.Linq;
using System.Text;
using System.IO;
using IronPython.Runtime.Exceptions;
using IronPython.Compiler;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using IronPython.Runtime.Operations;

namespace PyRevitLoader {

    // Writes a durable plain-text execution-error log that is always persisted to disk,
    // independent of the optional per-run logFilePath supplied to ExecuteScript.
    //
    // Log location : %LOCALAPPDATA%\pyRevit\Logs\ExecutionErrors\
    // File pattern  : execution-errors-YYYY-MM-DD.log   (one rolling file per calendar day)
    // Line format   : <ISO-8601 timestamp> [<severity>] <engine> | <script> | <message>
    //                 (newlines inside <message> are escaped as \n so every event is one line)
    //
    // The writer is intentionally resilient: every IO / formatting exception is silently
    // swallowed so that a logging failure can never break command execution.
    internal static class ExecutionErrorLog {
        // Subdirectory name used under %LOCALAPPDATA%\pyRevit\
        private const string LogSubdir = @"pyRevit\Logs\ExecutionErrors";

        // Returns the full path to today's rolling log file.
        private static string GetLogFilePath() {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(localApp, LogSubdir);
            // Use UTC date so the daily boundary is consistent across time zones and DST changes.
            string fileName = string.Format("execution-errors-{0}.log", DateTime.UtcNow.ToString("yyyy-MM-dd"));
            return Path.Combine(dir, fileName);
        }

        // Appends a single-line entry to the durable log.
        // severity  : e.g. "ERROR", "CANCEL"
        // scriptPath: full path of the script being executed (may be null)
        // engine    : short engine identifier, e.g. "IronPython"
        // message   : human-readable description; embedded newlines are escaped to \n
        internal static void Append(string severity, string scriptPath, string engine, string message) {
            try {
                string logPath = GetLogFilePath();
                string dir = Path.GetDirectoryName(logPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // UTC timestamp so entries are comparable across machines and time zones.
                string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                string safePath = string.IsNullOrEmpty(scriptPath) ? "<unknown>" : scriptPath;
                string safeMsg  = (message ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", @"\n");
                string safeEngine = string.IsNullOrEmpty(engine) ? "IronPython" : engine;

                string line = string.Format("{0} [{1}] {2} | {3} | {4}{5}",
                    ts, severity, safeEngine, safePath, safeMsg, Environment.NewLine);

                byte[] bytes = Encoding.UTF8.GetBytes(line);

                // Open with FileShare.ReadWrite so concurrent Revit instances can each append
                // to the same daily file without locking each other out.  Each Write call on
                // a FileMode.Append stream is positioned at the current end-of-file by the OS,
                // which is safe for short single-line payloads on NTFS.
                using (var fs = new FileStream(logPath,
                                               FileMode.Append,
                                               FileAccess.Write,
                                               FileShare.ReadWrite)) {
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch {
                // Never throw from the logger.
            }
        }
    }

    // Executes a script
    public class ScriptExecutor {
        private bool _fullframe = false;
        private readonly UIApplication _revit = null;

        public ScriptExecutor() {
        }

        public ScriptExecutor(UIApplication uiApplication, bool fullFrame = false) {
            _revit = uiApplication;
            _fullframe = fullFrame;
        }

        public string Message { get; private set; } = null;

#if PYREVITLABS_ENGINE
        public static string EnginePrefix => "pyRevitLabs.";
#else
        public static string EnginePrefix => "";
#endif

        public static string EngineVersion {
            get {
                var assmVersion = Assembly.GetAssembly(typeof(ScriptExecutor)).GetName().Version;
                return string.Format("{0}{1}{2}", assmVersion.Major, assmVersion.Minor, assmVersion.Build);
            }
        }

        public Result ExecuteScript(string sourcePath,
                                    IEnumerable<string> sysPaths = null,
                                    string logFilePath = null,
                                    IDictionary <string, object> variables = null) {
            try {
                var engine = CreateEngine();
                var scope = SetupEnvironment(engine);

                // Add script directory address to sys search paths
                if (sysPaths != null) {
                    var path = engine.GetSearchPaths();
                    foreach (var sysPath in sysPaths)
                        path.Add(sysPath);

                    engine.SetSearchPaths(path);
                }


                // set globals
                scope.SetVariable("__file__", sourcePath);

                if (variables != null)
                    foreach(var keyPair in variables)
                        scope.SetVariable(keyPair.Key, keyPair.Value);

                //var script = engine.CreateScriptSourceFromString(source, SourceCodeKind.Statements);
                var script = engine.CreateScriptSourceFromFile(sourcePath, Encoding.UTF8, SourceCodeKind.Statements);

                // setting module to be the main module so __name__ == __main__ is True
                var compiler_options = (PythonCompilerOptions)engine.GetCompilerOptions(scope);
                compiler_options.ModuleName = "__main__";
                compiler_options.Module |= IronPython.Runtime.ModuleOptions.Initialize;

                // Setting up error reporter and compile the script
                var errors = new ErrorReporter();
                var command = script.Compile(compiler_options, errors);
                if (command == null) {
                    // compilation failed, print errors and return
                    Message =
                        string.Join("\r\n", "IronPython Traceback:", string.Join("\r\n", errors.Errors.ToArray()));
                    if (logFilePath != null)
                        File.WriteAllText(logFilePath, Message);

                    // Always write a durable entry regardless of whether logFilePath was supplied.
                    ExecutionErrorLog.Append("CANCEL", sourcePath, "IronPython", Message);

                    return Result.Cancelled;
                }


                try {
                    script.Execute(scope);
                    return Result.Succeeded;
                }
                catch (SystemExitException) {
                    // ok, so the system exited. That was bound to happen...
                    return Result.Succeeded;
                }
                catch (Exception exception) {
                    string _dotnet_err_message = exception.ToString();
                    string _ipy_err_messages = engine.GetService<ExceptionOperations>().FormatException(exception);

                    _ipy_err_messages =
                        string.Join("\n", "IronPython Traceback:", _ipy_err_messages.Replace("\r\n", "\n"));
                    _dotnet_err_message =
                        string.Join("\n", "Script Executor Traceback:", _dotnet_err_message.Replace("\r\n", "\n"));

                    Message = _ipy_err_messages + "\n\n" + _dotnet_err_message;

                    // execution failed, log errors and return
                    if (logFilePath != null)
                        File.WriteAllText(logFilePath, Message);

                    // Always write a durable entry regardless of whether logFilePath was supplied.
                    ExecutionErrorLog.Append("ERROR", sourcePath, "IronPython", Message);

                    return Result.Failed;
                }
                finally {
                    engine.Runtime.Shutdown();
                    engine = null;
                }

            }
            catch (Exception ex) {
                Message = ex.ToString();
                // Always write a durable entry for host-level failures that bypass the inner try/catch.
                ExecutionErrorLog.Append("ERROR", sourcePath, "IronPython", Message);
                return Result.Failed;
            }
        }

        public ScriptEngine CreateEngine() {
            var flags = new Dictionary<string, object>();

            // default flags
            flags["LightweightScopes"] = true;

            if (_fullframe) {
                flags["Frames"] = true;
                flags["FullFrames"] = true;
            }

            var engine = IronPython.Hosting.Python.CreateEngine(flags);

            return engine;
        }

        public void AddEmbeddedLib(ScriptEngine engine) {
            // use embedded python lib
            var asm = this.GetType().Assembly;
#if PYREVITLABS_ENGINE
            string resName = string.Format("python_{0}pr_lib.zip", EngineVersion);
#else
            string resName = string.Format("python_{0}_lib.zip", EngineVersion);
#endif
            
            var resQuery = from name in asm.GetManifestResourceNames()
                           where name.ToLowerInvariant().EndsWith(resName)
                           select name;

            var importer = new IronPython.Modules.ResourceMetaPathImporter(asm, resQuery.Single());
            dynamic sys = IronPython.Hosting.Python.GetSysModule(engine);
            sys.meta_path.append(importer);
        }

        // Set up an IronPython environment
        public ScriptScope SetupEnvironment(ScriptEngine engine) {
            var scope = IronPython.Hosting.Python.CreateModule(engine, "__main__");

            SetupEnvironment(engine, scope);

            return scope;
        }

        public void SetupEnvironment(ScriptEngine engine, ScriptScope scope) {
            // add two special variables: __revit__ and __vars__ to be globally visible everywhere:            
            var builtin = IronPython.Hosting.Python.GetBuiltinModule(engine);
            builtin.SetVariable("__revit__", _revit);

            // add the search paths
            AddEmbeddedLib(engine);

            // reference RevitAPI and RevitAPIUI
            engine.Runtime.LoadAssembly(typeof(Autodesk.Revit.DB.Document).Assembly);
            engine.Runtime.LoadAssembly(typeof(Autodesk.Revit.UI.UIApplication).Assembly);
        }
    }
}
