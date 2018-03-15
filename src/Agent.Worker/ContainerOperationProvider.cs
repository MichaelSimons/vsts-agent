using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Expressions = Microsoft.TeamFoundation.DistributedTask.Orchestration.Server.Expressions;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Handlers;
using Microsoft.VisualStudio.Services.Agent.Worker.Container;
using System.Threading;
using System.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(ContainerOperationProvider))]
    public interface IContainerOperationProvider : IAgentService
    {
        IStep GetContainerStartStep(IExecutionContext jobContext);
        IStep GetContainerStopStep(IExecutionContext jobContext);
    }

    public class ContainerOperationProvider : AgentService, IContainerOperationProvider
    {
        private IDockerCommandManager _dockerManger;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _dockerManger = HostContext.GetService<IDockerCommandManager>();
        }

        public IStep GetContainerStartStep(IExecutionContext jobContext)
        {
            return new JobExtensionRunner(context: jobContext.CreateChild(Guid.NewGuid(), StringUtil.Loc("InitializeContainer"), nameof(ContainerOperationProvider)),
                                          runAsync: StartContainerAsync,
                                          condition: ExpressionManager.Succeeded,
                                          displayName: StringUtil.Loc("InitializeContainer"));
        }

        public IStep GetContainerStopStep(IExecutionContext jobContext)
        {
            return new JobExtensionRunner(context: jobContext.CreateChild(Guid.NewGuid(), StringUtil.Loc("StopContainer"), nameof(ContainerOperationProvider)),
                                          runAsync: StopContainerAsync,
                                          condition: ExpressionManager.Always,
                                          displayName: StringUtil.Loc("StopContainer"));
        }

        private async Task StartContainerAsync(IExecutionContext executionContext)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(executionContext.Container, nameof(executionContext.Container));
            ArgUtil.NotNullOrEmpty(executionContext.Container.ContainerImage, nameof(executionContext.Container.ContainerImage));

            Trace.Info($"Container name: {executionContext.Container.ContainerName}");
            Trace.Info($"Container image: {executionContext.Container.ContainerImage}");
            Trace.Info($"Container registry: {executionContext.Container.ContainerRegistryEndpoint}");
            Trace.Info($"Container options: {executionContext.Container.ContainerCreateOptions}");
            Trace.Info($"Skip container image pull: {executionContext.Container.SkipContainerImagePull}");

            // Check Windows version
            if (Environment.OSVersion.Version < new Version(10, 0, 17093))
            {
                throw new NotSupportedException($"Running VSTS job in container require Windows 10.0.17093.0 or higher, current Windows version '{Environment.OSVersion.VersionString}'.");
            }

            // Check docker client/server version
            DockerVersion dockerVersion = await _dockerManger.DockerVersion(executionContext);
            ArgUtil.NotNull(dockerVersion.ServerVersion, nameof(dockerVersion.ServerVersion));
            ArgUtil.NotNull(dockerVersion.ClientVersion, nameof(dockerVersion.ClientVersion));

#if OS_WINDOWS            
            Version requiredDockerVersion = new Version(17, 6);
#else
            Version requiredDockerVersion = new Version(17, 12);
#endif

            if (dockerVersion.ServerVersion < requiredDockerVersion)
            {
                throw new NotSupportedException(StringUtil.Loc("MinRequiredDockerServerVersion", requiredDockerVersion, _dockerManger.DockerPath, dockerVersion.ServerVersion));
            }
            if (dockerVersion.ClientVersion < requiredDockerVersion)
            {
                throw new NotSupportedException(StringUtil.Loc("MinRequiredDockerClientVersion", requiredDockerVersion, _dockerManger.DockerPath, dockerVersion.ClientVersion));
            }

            // Login to private docker registry
            string registryServer = string.Empty;
            if (!string.IsNullOrEmpty(executionContext.Container.ContainerRegistryEndpoint))
            {
                var registryEndpoint = executionContext.Endpoints.FirstOrDefault(x => x.Type == "dockerregistry" && String.Equals(x.Id.ToString(), executionContext.Container.ContainerRegistryEndpoint, StringComparison.OrdinalIgnoreCase));
                ArgUtil.NotNull(registryEndpoint, nameof(registryEndpoint));

                string username = string.Empty;
                string password = string.Empty;
                registryEndpoint.Authorization?.Parameters?.TryGetValue("registry", out registryServer);
                registryEndpoint.Authorization?.Parameters?.TryGetValue("username", out username);
                registryEndpoint.Authorization?.Parameters?.TryGetValue("password", out password);

                ArgUtil.NotNullOrEmpty(registryServer, nameof(registryServer));
                ArgUtil.NotNullOrEmpty(username, nameof(username));
                ArgUtil.NotNullOrEmpty(password, nameof(password));

                int loginExitCode = await _dockerManger.DockerLogin(executionContext, registryServer, username, password);
                if (loginExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker login fail with exit code {loginExitCode}");
                }
            }

            if (!executionContext.Container.SkipContainerImagePull)
            {
                string imageName = executionContext.Container.ContainerImage;
                if (!string.IsNullOrEmpty(registryServer) &&
                    registryServer.IndexOf("index.docker.io", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !imageName.StartsWith(registryServer, StringComparison.OrdinalIgnoreCase))
                {
                    imageName = $"{registryServer}/{imageName}";
                }

                // Pull down docker image
                int pullExitCode = await _dockerManger.DockerPull(executionContext, imageName);
                if (pullExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker pull failed with exit code {pullExitCode}");
                }
            }

            // Mount folder into container            
            executionContext.Container.MountVolumes.Add(new MountVolume(executionContext.Variables.Agent_TempDirectory));
            executionContext.Container.MountVolumes.Add(new MountVolume(executionContext.Variables.Agent_ToolsDirectory));
#if OS_WINDOWS
            executionContext.Container.MountVolumes.Add(new MountVolume(HostContext.GetDirectory(WellKnownDirectory.Externals)));
            executionContext.Container.MountVolumes.Add(new MountVolume(HostContext.GetDirectory(WellKnownDirectory.Work)));
#else
            executionContext.Container.MountVolumes.Add(new MountVolume(HostContext.GetDirectory(WellKnownDirectory.Tasks)));
            executionContext.Container.MountVolumes.Add(new MountVolume(Path.GetDirectoryName(executionContext.Variables.System_DefaultWorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
            executionContext.Container.MountVolumes.Add(new MountVolume(HostContext.GetDirectory(WellKnownDirectory.Externals), true));
            
            // Ensure .taskkey file exist so we can mount it.
            string taskKeyFile = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), ".taskkey");
            if (!File.Exists(taskKeyFile))
            {
                File.WriteAllText(taskKeyFile, string.Empty);
            }
            executionContext.Container.MountVolumes.Add(new MountVolume(taskKeyFile));
#endif
            try
            {
                int pullExitCode = await _dockerManger.DockerCreate(executionContext, executionContext.Container);
                if (pullExitCode != 0)
                {
                    executionContext.Error($"Docker create fail with exit code {pullExitCode}");
                }

                ArgUtil.NotNullOrEmpty(executionContext.Container.ContainerId, nameof(executionContext.Container.ContainerId));
                executionContext.Variables.Set(Constants.Variables.Agent.ContainerId, executionContext.Container.ContainerId);

                // Start container
                int startExitCode = await _dockerManger.DockerStart(executionContext, executionContext.Container.ContainerId);
                if (startExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker start fail with exit code {startExitCode}");
                }
            }
            finally
            {
                // Logout for private registry
                if (!string.IsNullOrEmpty(registryServer))
                {
                    int logoutExitCode = await _dockerManger.DockerLogout(executionContext, registryServer);
                    if (logoutExitCode != 0)
                    {
                        executionContext.Error($"Docker logout fail with exit code {logoutExitCode}");
                    }
                }
            }

#if !OS_WINDOWS
            // Ensure bash exist in the image
            int execWhichBashExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"which bash");
            if (execWhichBashExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execWhichBashExitCode}");
            }

            // Get current username
            executionContext.Container.CurrentUserName = (await ExecuteCommandAsync(executionContext, "whoami", string.Empty)).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(executionContext.Container.CurrentUserName, nameof(executionContext.Container.CurrentUserName));

            // Get current userId
            executionContext.Container.CurrentUserId = (await ExecuteCommandAsync(executionContext, "id", $"-u {executionContext.Container.CurrentUserName}")).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(executionContext.Container.CurrentUserId, nameof(executionContext.Container.CurrentUserId));

            executionContext.Output(StringUtil.Loc("CreateUserWithSameUIDInsideContainer", executionContext.Container.CurrentUserId));

            // Create an user with same uid as the agent run as user inside the container.
            // All command execute in docker will run as Root by default, 
            // this will cause the agent on the host machine doesn't have permission to any new file/folder created inside the container.
            // So, we create a user account with same UID inside the container and let all docker exec command run as that user.
            string containerUserName = string.Empty;

            // We need to find out whether there is a user with same UID inside the container
            List<string> userNames = new List<string>();
            int execGrepExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"bash -c \"grep {executionContext.Container.CurrentUserId} /etc/passwd | cut -f1 -d:\"", userNames);
            if (execGrepExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execGrepExitCode}");
            }

            if (userNames.Count > 0)
            {
                // check all potential username that might match the UID.
                foreach (string username in userNames)
                {
                    int execIdExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"id -u {username}");
                    if (execIdExitCode == 0)
                    {
                        containerUserName = username;
                        break;
                    }
                }
            }

            // Create a new user with same UID
            if (string.IsNullOrEmpty(containerUserName))
            {
                containerUserName = $"{executionContext.Container.CurrentUserName}_VSTSContainer";
                int execUseraddExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"useradd -m -u {executionContext.Container.CurrentUserId} {containerUserName}");
                if (execUseraddExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker exec fail with exit code {execUseraddExitCode}");
                }
            }

            executionContext.Output(StringUtil.Loc("GrantContainerUserSUDOPrivilege", containerUserName));

            // Create a new vsts_sudo group for giving sudo permission
            int execGroupaddExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"groupadd VSTS_Container_SUDO");
            if (execGroupaddExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execGroupaddExitCode}");
            }

            // Add the new created user to the new created VSTS_SUDO group.
            int execUsermodExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"usermod -a -G VSTS_Container_SUDO {containerUserName}");
            if (execUsermodExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execUsermodExitCode}");
            }

            // Allow the new vsts_sudo group run any sudo command without providing password.
            int execEchoExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"su -c \"echo '%VSTS_Container_SUDO ALL=(ALL:ALL) NOPASSWD:ALL' >> /etc/sudoers\"");
            if (execUsermodExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execEchoExitCode}");
            }
#endif
        }

        private async Task StopContainerAsync(IExecutionContext executionContext)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(executionContext.Container, nameof(executionContext.Container));

            if (!string.IsNullOrEmpty(executionContext.Container.ContainerId))
            {
                executionContext.Output($"Stop container: {executionContext.Container.ContainerDisplayName}");

                int stopExitCode = await _dockerManger.DockerStop(executionContext, executionContext.Container.ContainerId);
                if (stopExitCode != 0)
                {
                    executionContext.Error($"Docker stop fail with exit code {stopExitCode}");
                }

                int removeExitCode = await _dockerManger.DockerNetworkRemove(executionContext);
                if (removeExitCode != 0)
                {
                    executionContext.Error($"Docker network rm fail with exit code {removeExitCode}");
                }
            }
        }

#if !OS_WINDOWS
        private async Task<List<string>> ExecuteCommandAsync(IExecutionContext context, string command, string arg)
        {
            context.Command($"{command} {arg}");

            List<string> outputs = new List<string>();
            object outputLock = new object();
            var processInvoker = HostContext.CreateService<IProcessInvoker>();
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        outputs.Add(message.Data);
                    }
                }
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        outputs.Add(message.Data);
                    }
                }
            };

            await processInvoker.ExecuteAsync(
                            workingDirectory: context.Variables.Agent_WorkFolder,
                            fileName: command,
                            arguments: arg,
                            environment: null,
                            requireExitCodeZero: true,
                            outputEncoding: null,
                            cancellationToken: CancellationToken.None);

            foreach (var outputLine in outputs)
            {
                context.Output(outputLine);
            }

            return outputs;
        }
#endif
    }
}
