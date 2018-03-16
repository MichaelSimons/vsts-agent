using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Container;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    public interface IStepHost : IAgentService
    {
        event EventHandler<ProcessDataReceivedEventArgs> OutputDataReceived;
        event EventHandler<ProcessDataReceivedEventArgs> ErrorDataReceived;

        Task<int> ExecuteAsync(string workingDirectory,
                               string fileName,
                               string arguments,
                               IDictionary<string, string> environment,
                               bool requireExitCodeZero,
                               Encoding outputEncoding,
                               bool killProcessOnCancel,
                               CancellationToken cancellationToken);
    }

    [ServiceLocator(Default = typeof(ContainerStepHost))]
    public interface IContainerStepHost : IStepHost
    {
        ContainerInfo Container { get; set; }
    }

    [ServiceLocator(Default = typeof(DefaultStepHost))]
    public interface IDefaultStepHost : IStepHost
    {
    }


    public sealed class DefaultStepHost : AgentService, IDefaultStepHost
    {
        public event EventHandler<ProcessDataReceivedEventArgs> OutputDataReceived;
        public event EventHandler<ProcessDataReceivedEventArgs> ErrorDataReceived;

        public async Task<int> ExecuteAsync(string workingDirectory,
                                            string fileName,
                                            string arguments,
                                            IDictionary<string, string> environment,
                                            bool requireExitCodeZero,
                                            Encoding outputEncoding,
                                            bool killProcessOnCancel,
                                            CancellationToken cancellationToken)
        {
            using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
            {
                processInvoker.OutputDataReceived += OutputDataReceived;
                processInvoker.ErrorDataReceived += ErrorDataReceived;

                return await processInvoker.ExecuteAsync(workingDirectory: workingDirectory,
                                                         fileName: fileName,
                                                         arguments: arguments,
                                                         environment: environment,
                                                         requireExitCodeZero: requireExitCodeZero,
                                                         outputEncoding: outputEncoding,
                                                         killProcessOnCancel: killProcessOnCancel,
                                                         cancellationToken: cancellationToken);
            }
        }
    }

    public sealed class ContainerStepHost : AgentService, IContainerStepHost
    {
        public ContainerInfo Container { get; set; }
        public event EventHandler<ProcessDataReceivedEventArgs> OutputDataReceived;
        public event EventHandler<ProcessDataReceivedEventArgs> ErrorDataReceived;

        public async Task<int> ExecuteAsync(string workingDirectory,
                                            string fileName,
                                            string arguments,
                                            IDictionary<string, string> environment,
                                            bool requireExitCodeZero,
                                            Encoding outputEncoding,
                                            bool killProcessOnCancel,
                                            CancellationToken cancellationToken)
        {
            // make sure container exist.
            ArgUtil.NotNull(Container, nameof(Container));
            ArgUtil.NotNullOrEmpty(Container.ContainerId, nameof(Container.ContainerId));

            var dockerManger = HostContext.GetService<IDockerCommandManager>();
            string containerEnginePath = dockerManger.DockerPath;

            string envOptions = "";
            foreach (var env in environment)
            {
                envOptions += $" -e \"{env.Key}={env.Value.Replace("\"", "\\\"")}\"";
            }

#if !OS_WINDOWS
            string workingDirectoryEscaped = StringUtil.Format(@"""{0}""", workingDirectory);
            string filePathEscaped = StringUtil.Format(@"""{0}""", fileName);
            string argumentsEscaped = arguments;
            string containerExecutionArgs = $"exec -u {Container.CurrentUserId} {envOptions} -w {workingDirectoryEscaped} {Container.ContainerId} {filePathEscaped} {argumentsEscaped}";
#else
            string workingDirectoryEscaped = StringUtil.Format(@"""{0}""", workingDirectory); // -w when docker 17.12 released for windows
            string filePathEscaped = StringUtil.Format(@"""{0}""", fileName);
            string argumentsEscaped = arguments;
            string containerExecutionArgs = $"exec {envOptions} {Container.ContainerId} {filePathEscaped} {argumentsEscaped}";
#endif

            using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
            {
                processInvoker.OutputDataReceived += OutputDataReceived;
                processInvoker.ErrorDataReceived += ErrorDataReceived;

                return await processInvoker.ExecuteAsync(workingDirectory: HostContext.GetDirectory(WellKnownDirectory.Work),
                                                         fileName: containerEnginePath,
                                                         arguments: containerExecutionArgs,
                                                         environment: null,
                                                         requireExitCodeZero: requireExitCodeZero,
                                                         outputEncoding: outputEncoding,
                                                         killProcessOnCancel: killProcessOnCancel,
                                                         cancellationToken: cancellationToken);
            }
        }
    }
}