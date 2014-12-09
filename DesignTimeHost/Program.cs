﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Framework.DesignTimeHost.Models;
using Microsoft.Framework.DesignTimeHost.Models.IncomingMessages;
using Microsoft.Framework.DesignTimeHost.Models.OutgoingMessages;
using Newtonsoft.Json.Linq;

namespace DesignTimeHostDemo
{
    public class Program
    {
        public event Action<IEnumerable<string>> OnUpdateSourceFileReference;
        public event Action<IEnumerable<string>> OnUpdateFileReference;

        string hostId = Guid.NewGuid().ToString();
        // this can be k10
        string activeTargetFramework = "net45";
        int contextId = 0;
        Dictionary<int, string> mapping = new Dictionary<int, string>();

        Dictionary<string, int> projects = new Dictionary<string, int>();

        ProcessingQueue queue;

        public void Go(string applicationRoot, Action<string> log)
        {
            var runtimePath = GetRuntimePath(log);

            var port = 1334;

            // Show runtime output
            var showRuntimeOutput = true;

            StartRuntime(runtimePath, applicationRoot, hostId, port, showRuntimeOutput, () =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Connect(new IPEndPoint(IPAddress.Loopback, port));

                    var networkStream = new NetworkStream(socket);

                    log("Connected to design time host");

                    queue = new ProcessingQueue(networkStream);

                    queue.OnReceive += m =>
                    {
                        if (m.MessageType == "ProjectInformation")
                        {
                            log(m.ToString());
                        }
                        // Get the project associated with this message
                        var projectPath = mapping[m.ContextId];
                        log("Project path - " + projectPath);
                        log("Message type ------------------- " + m.MessageType);

                        // This is where we can handle messages and update the
                        // language service
                        if (m.MessageType == "References")
                        {
                            // References as well as the dependency graph information
                            var val = m.Payload.ToObject<ReferencesMessage>();
                          
                            OnUpdateFileReference(val.FileReferences);
                        }
                        else if (m.MessageType == "Diagnostics")
                        {
                            // Errors and warnings
                            var val = m.Payload.ToObject<DiagnosticsMessage>();
                            foreach (var error in val.Errors)
                            {
                                Console.WriteLine(error);
                            }

                            foreach (var warning in val.Warnings)
                            {
                                Console.WriteLine(warning);
                            }
                        }
                        else if (m.MessageType == "Configurations")
                        {
                            // Configuration and compiler options
                            // var val = m.Payload.ToObject<ConfigurationsMessage>();
                        }
                        else if (m.MessageType == "Sources")
                        {
                            // The sources to feed to the language service
                            var val = m.Payload.ToObject<SourcesMessage>();
                            OnUpdateSourceFileReference(val.Files);
                        }
                    };

                    // Start the message channel
                    queue.Start();

                    var solutionPath = applicationRoot;
                    var watcher = new FileWatcher(solutionPath);
//
//                    foreach (var projectFile in Directory.EnumerateFiles(solutionPath, "project.json", SearchOption.AllDirectories))
//                    {
//                    }
//
                    // When there's a file change
                    watcher.OnChanged += changedPath =>
                    {
                        foreach (var project in projects)
                        {
                            // If the project changed
                            if (changedPath.StartsWith(project.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                queue.Post(new Message
                                    {
                                        ContextId = project.Value,
                                        MessageType = "FilesChanged",
                                        HostId = hostId
                                    });
                            }
                        }
                    };
                });
        }

        string GetRuntimePath(Action<string> log)
        {
            var kreHome = Path.Combine(Environment.GetEnvironmentVariable("HOME"), ".kre");
            log("KRE Home = " + kreHome);
            var defaultAlias = Path.Combine(kreHome, "alias", "default.alias");
            log("Using default alias = " + defaultAlias);
            var version = File.ReadAllText(defaultAlias).Trim();
            log("Using KRE version = " + version);
            // TODO: Make this work on windows
            var runtimePath = Path.Combine(kreHome, "packages", version);
            log("Using KRE at = " + runtimePath);
            return runtimePath;
        }

        public void RegisterProject(string projectFile)
        {
            string projectPath = Path.GetDirectoryName(projectFile).TrimEnd(Path.DirectorySeparatorChar);

            // Send an InitializeMessage for each project
            var initializeMessage = new InitializeMessage
            {
                ProjectFolder = projectPath,
                // ??? 
                Configuration = activeTargetFramework
            };

            // Create a unique id for this project
            int projectContextId = contextId++;

            // Create a mapping from path to contextid and back
            projects[projectPath] = projectContextId;
            mapping[projectContextId] = projectPath;

            // Initialize this project
            queue.Post(new Message
                {
                    ContextId = projectContextId,
                    MessageType = "Initialize",
                    Payload = JToken.FromObject(initializeMessage),
                    HostId = hostId
                });

            // Watch the project.json file
            // watcher.WatchFile(Path.Combine(projectPath, "project.json"));
            // watcher.WatchDirectory(projectPath, ".cs");

            // // Watch all directories for cs files

            // foreach (var cs in Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories))
            // {
            //     watcher.WatchFile(cs);
            // }

            // foreach (var d in Directory.GetDirectories(projectPath, "*.*", SearchOption.AllDirectories))
            // {
            //     watcher.WatchDirectory(d, ".cs");
            // }
                    
        }

        private static void StartRuntime(string runtimePath,
            string applicationPath,
            string hostId,
            int port,
            bool verboseOutput,
            Action onStart)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(runtimePath, "bin", "klr"),
                Arguments = String.Format(@"{0} {1} {2} {3}",
                    Path.Combine(runtimePath, "bin", "lib", "Microsoft.Framework.DesignTimeHost", "Microsoft.Framework.DesignTimeHost.dll"),
                    port,
                    Process.GetCurrentProcess().Id,
                    hostId),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = applicationPath
            };

            Console.WriteLine(psi.FileName + " " + psi.Arguments);
            Environment.SetEnvironmentVariable("KRE_APPBASE", applicationPath);

            var kreProcess = Process.Start(psi);
            kreProcess.BeginOutputReadLine();
            kreProcess.BeginErrorReadLine();
            kreProcess.OutputDataReceived += (sender, e) =>
            {
                if (verboseOutput)
                {
                    Console.WriteLine(e.Data);
                }
            };

            // Wait a little bit for it to connect before firing the callback
            Thread.Sleep(1000);

            if (kreProcess.HasExited)
            {
                Console.WriteLine("Child process failed with {0}", kreProcess.ExitCode);
                return;
            }

            kreProcess.EnableRaisingEvents = true;
            kreProcess.Exited += (sender, e) =>
            {
                Console.WriteLine("Process crash trying again");

                Thread.Sleep(1000);

                StartRuntime(runtimePath, applicationPath, hostId, port, verboseOutput, onStart);
            };

            onStart();
        }

        private static Task ConnectAsync(Socket socket, IPEndPoint endPoint)
        {
            return Task.Factory.FromAsync((cb, state) => socket.BeginConnect(endPoint, cb, state), ar => socket.EndConnect(ar), null);
        }
    }
}
