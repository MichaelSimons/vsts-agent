using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Container
{
    [ServiceLocator(Default = typeof(DockerCommandManager))]
    public interface IDockerCommandManager : IAgentService
    {
        string DockerPath { get; }
        Task<DockerVersion> DockerVersion(IExecutionContext context);
        Task<int> DockerLogin(IExecutionContext context, string server, string username, string password);
        Task<int> DockerLogout(IExecutionContext context, string server);
        Task<int> DockerPull(IExecutionContext context, string image);
        Task<int> DockerCreate(IExecutionContext context, ContainerInfo container);
        Task<int> DockerStart(IExecutionContext context, string containerId);
        Task<int> DockerStop(IExecutionContext context, string containerId);
        Task<int> DockerNetworkRemove(IExecutionContext context);
        Task<int> DockerExec(IExecutionContext context, string containerId, string options, string command);
        Task<int> DockerExec(IExecutionContext context, string containerId, string options, string command, List<string> outputs);
    }

    public class DockerCommandManager : AgentService, IDockerCommandManager
    {
        private string _dockerNetwork;

        public string DockerPath { get; private set; }

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);

            var whichUtil = HostContext.GetService<IWhichUtil>();
            DockerPath = whichUtil.Which("docker", true);
        }

        public async Task<DockerVersion> DockerVersion(IExecutionContext context)
        {
            string serverVersionStr = (await ExecuteDockerCommandAsync(context, "version", "--format '{{.Server.Version}}'")).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(serverVersionStr, "Docker.Server.Version");
            context.Output($"{serverVersionStr}");

            string clientVersionStr = (await ExecuteDockerCommandAsync(context, "version", "--format '{{.Client.Version}}'")).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(serverVersionStr, "Docker.Client.Version");
            context.Output($"{clientVersionStr}");

            // we interested about major.minor.patch version
            Regex verRegex = new Regex("\\d+\\.\\d+(\\.\\d+)?", RegexOptions.IgnoreCase);

            Version serverVersion = null;
            var serverVersionMatchResult = verRegex.Match(serverVersionStr);
            if (serverVersionMatchResult.Success && !string.IsNullOrEmpty(serverVersionMatchResult.Value))
            {
                if (!Version.TryParse(serverVersionMatchResult.Value, out serverVersion))
                {
                    serverVersion = null;
                }
            }

            Version clientVersion = null;
            var clientVersionMatchResult = verRegex.Match(serverVersionStr);
            if (clientVersionMatchResult.Success && !string.IsNullOrEmpty(clientVersionMatchResult.Value))
            {
                if (!Version.TryParse(clientVersionMatchResult.Value, out clientVersion))
                {
                    clientVersion = null;
                }
            }

            return new DockerVersion(serverVersion, clientVersion);
        }

        public async Task<int> DockerLogin(IExecutionContext context, string server, string username, string password)
        {
            return await ExecuteDockerCommandAsync(context, "login", $"--username \"{username}\" --password \"{password}\" {server}", context.CancellationToken);
        }

        public async Task<int> DockerLogout(IExecutionContext context, string server)
        {
            return await ExecuteDockerCommandAsync(context, "logout", $"{server}", context.CancellationToken);
        }

        public async Task<int> DockerPull(IExecutionContext context, string image)
        {
            return await ExecuteDockerCommandAsync(context, "pull", image, context.CancellationToken);
        }

        public async Task<int> DockerCreate(IExecutionContext context, ContainerInfo container)
        {
            // Create local docker network share across all following docker create
            if (string.IsNullOrEmpty(_dockerNetwork))
            {
                _dockerNetwork = $"vsts_network_{Guid.NewGuid().ToString("D")}";
                int networkExitCode = await ExecuteDockerCommandAsync(context, "network", $"create {_dockerNetwork}", context.CancellationToken);
                if (networkExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker network create failed with exit code {networkExitCode}");
                }

                // Expose docker network to env
                context.Variables.Set(Constants.Variables.Agent.ContainerNetwork, _dockerNetwork);
            }

            string dockerMountVolumesArgs = string.Empty;
            if (container.MountVolumes.Count > 0)
            {
                foreach (var volume in container.MountVolumes)
                {
                    // replace `"` with `\"` and add `"{0}"` to all path.
                    dockerMountVolumesArgs += $" -v \"{volume.VolumePath.Replace("\"", "\\\"")}\":\"{volume.VolumePath.Replace("\"", "\\\"")}\"";
                    if (volume.ReadOnly)
                    {
                        dockerMountVolumesArgs += ":ro";
                    }
                }
            }

#if !OS_WINDOWS
            string dockerArgs = $"--name {container.ContainerDisplayName} --rm --network={_dockerNetwork} -v /var/run/docker.sock:/var/run/docker.sock {container.ContainerCreateOptions} {dockerMountVolumesArgs} {container.ContainerImage} sleep 999d";
#else
            string dockerArgs = $"--name {container.ContainerDisplayName} --rm {container.ContainerCreateOptions} {dockerMountVolumesArgs} {container.ContainerImage} ping -t 127.0.0.1";  // add -v '\\.\pipe\docker_engine:\\.\pipe\docker_engine' when 17.09 available 
#endif
            List<string> outputStrings = new List<string>();
            int dockerCreateExitCode = await ExecuteDockerCommandAsync(context, "create", dockerArgs, outputStrings);
            context.Output($"{string.Join(Environment.NewLine, outputStrings)}");
            if (dockerCreateExitCode == 0)
            {
                container.ContainerId = outputStrings.First(x => !string.IsNullOrEmpty(x));
            }

            return dockerCreateExitCode;
        }

        public async Task<int> DockerStart(IExecutionContext context, string containerId)
        {
            return await ExecuteDockerCommandAsync(context, "start", containerId, context.CancellationToken);
        }

        public async Task<int> DockerStop(IExecutionContext context, string containerId)
        {
            return await ExecuteDockerCommandAsync(context, "stop", containerId, context.CancellationToken);
        }

        public async Task<int> DockerNetworkRemove(IExecutionContext context)
        {
            return await ExecuteDockerCommandAsync(context, "network", $"rm {_dockerNetwork}", context.CancellationToken);
        }

        public async Task<int> DockerExec(IExecutionContext context, string containerId, string options, string command)
        {
            return await ExecuteDockerCommandAsync(context, "exec", $"{options} {containerId} {command}", context.CancellationToken);
        }

        public async Task<int> DockerExec(IExecutionContext context, string containerId, string options, string command, List<string> output)
        {
            ArgUtil.NotNull(output, nameof(output));

            string arg = $"exec {options} {containerId} {command}".Trim();
            context.Command($"{DockerPath} {arg}");

            object outputLock = new object();
            var processInvoker = HostContext.CreateService<IProcessInvoker>();
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        output.Add(message.Data);
                    }
                }
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        output.Add(message.Data);
                    }
                }
            };

            return await processInvoker.ExecuteAsync(
                            workingDirectory: HostContext.GetDirectory(WellKnownDirectory.Work),
                            fileName: DockerPath,
                            arguments: arg,
                            environment: null,
                            requireExitCodeZero: false,
                            outputEncoding: null,
                            cancellationToken: CancellationToken.None);
        }

        private async Task<int> ExecuteDockerCommandAsync(IExecutionContext context, string command, string options, CancellationToken cancellationToken = default(CancellationToken))
        {
            string arg = $"{command} {options}".Trim();
            context.Command($"{DockerPath} {arg}");

            var processInvoker = HostContext.CreateService<IProcessInvoker>();
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                context.Output(message.Data);
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                context.Output(message.Data);
            };

            return await processInvoker.ExecuteAsync(
                workingDirectory: HostContext.GetDirectory(WellKnownDirectory.Work),
                fileName: DockerPath,
                arguments: arg,
                environment: null,
                requireExitCodeZero: false,
                outputEncoding: null,
                cancellationToken: cancellationToken);
        }

        private async Task<List<string>> ExecuteDockerCommandAsync(IExecutionContext context, string command, string options)
        {
            string arg = $"{command} {options}".Trim();
            context.Command($"{DockerPath} {arg}");

            List<string> output = new List<string>();
            object outputLock = new object();
            var processInvoker = HostContext.CreateService<IProcessInvoker>();
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        output.Add(message.Data);
                    }
                }
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        output.Add(message.Data);
                    }
                }
            };

            await processInvoker.ExecuteAsync(
                            workingDirectory: HostContext.GetDirectory(WellKnownDirectory.Work),
                            fileName: DockerPath,
                            arguments: arg,
                            environment: null,
                            requireExitCodeZero: true,
                            outputEncoding: null,
                            cancellationToken: CancellationToken.None);

            return output;
        }

        private async Task<int> ExecuteDockerCommandAsync(IExecutionContext context, string command, string options, IList<string> output)
        {
            string arg = $"{command} {options}".Trim();
            context.Command($"{DockerPath} {arg}");

            if (output == null)
            {
                output = new List<string>();
            }

            object outputLock = new object();
            var processInvoker = HostContext.CreateService<IProcessInvoker>();
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        output.Add(message.Data);
                    }
                }
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        output.Add(message.Data);
                    }
                }
            };

            return await processInvoker.ExecuteAsync(
                            workingDirectory: HostContext.GetDirectory(WellKnownDirectory.Work),
                            fileName: DockerPath,
                            arguments: arg,
                            environment: null,
                            requireExitCodeZero: false,
                            outputEncoding: null,
                            cancellationToken: CancellationToken.None);
        }
    }
}