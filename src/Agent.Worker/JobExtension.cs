using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.Orchestration.Server.Expressions;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;
using System.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    public interface IJobExtension : IExtension
    {
        HostTypes HostType { get; }
        Task<List<IStep>> InitializeJob(IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message);
        string GetRootedPath(IExecutionContext context, string path);
        void ConvertLocalPath(IExecutionContext context, string localPath, out string repoName, out string sourcePath);
    }

    public abstract class JobExtension : AgentService, IJobExtension
    {
        public abstract HostTypes HostType { get; }

        public abstract Type ExtensionType { get; }

        // Anything job extension want to do before building the steps list.
        public abstract void InitializeJobExtension(IExecutionContext context);

        // Anything job extension want to add to pre-job steps list. This will be deprecated when GetSource move to a task.
        public abstract IStep GetExtensionPreJobStep(IExecutionContext jobContext);

        // Anything job extension want to add to post-job steps list. This will be deprecated when GetSource move to a task.
        public abstract IStep GetExtensionPostJobStep(IExecutionContext jobContext);

        public abstract string GetRootedPath(IExecutionContext context, string path);

        public abstract void ConvertLocalPath(IExecutionContext context, string localPath, out string repoName, out string sourcePath);

        // download all required tasks.
        // make sure all task's condition inputs are valid.
        // build up a list of steps for jobrunner. (pre-job + job + post-job)
        public async Task<List<IStep>> InitializeJob(IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message)
        {
            Trace.Entering();
            ArgUtil.NotNull(jobContext, nameof(jobContext));
            ArgUtil.NotNull(message, nameof(message));

            // create a new timeline record node for 'Initialize job'
            IExecutionContext context = jobContext.CreateChild(Guid.NewGuid(), StringUtil.Loc("InitializeJob"), nameof(JobExtension));

            List<IStep> preJobSteps = new List<IStep>();
            List<IStep> jobSteps = new List<IStep>();
            List<IStep> postJobSteps = new List<IStep>();
            using (var register = jobContext.CancellationToken.Register(() => { context.CancelToken(); }))
            {
                try
                {
                    context.Start();
                    context.Section(StringUtil.Loc("StepStarting", StringUtil.Loc("InitializeJob")));

                    // Give job extension a chance to initialize
                    Trace.Info($"Run initial step from extension {this.GetType().Name}.");
                    InitializeJobExtension(context);

                    // Download tasks if not already in the cache
                    Trace.Info("Downloading task definitions.");
                    var taskManager = HostContext.GetService<ITaskManager>();
                    await taskManager.DownloadAsync(context, message.Steps);

                    // Parse all Task conditions.
                    Trace.Info("Parsing all task's condition inputs.");
                    var expression = HostContext.GetService<IExpressionManager>();
                    Dictionary<Guid, INode> taskConditionMap = new Dictionary<Guid, INode>();
                    foreach (var task in message.Steps.Where(x => x.Type == Pipelines.StepType.Task))
                    {
                        INode condition;
                        if (!string.IsNullOrEmpty(task.Condition))
                        {
                            context.Debug($"Task '{task.DisplayName}' has following condition: '{task.Condition}'.");
                            condition = expression.Parse(context, task.Condition);
                        }
                        else
                        {
                            condition = ExpressionManager.Succeeded;
                        }

                        taskConditionMap[task.Id] = condition;
                    }

#if OS_WINDOWS
                    // This is for internal testing and is not publicly supported. This will be removed from the agent at a later time.
                    var prepareScript = Environment.GetEnvironmentVariable("VSTS_AGENT_INIT_INTERNAL_TEMP_HACK");
                    ServiceEndpoint systemConnection = context.Endpoints.Single(x => string.Equals(x.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(prepareScript) && context.Container == null)
                    {
                        var prepareStep = new ManagementScriptStep(
                            scriptPath: prepareScript,
                            condition: ExpressionManager.Succeeded,
                            displayName: "Agent Initialization");

                        Trace.Verbose($"Adding agent init script step.");
                        prepareStep.Initialize(HostContext);
                        prepareStep.ExecutionContext = jobContext.CreateChild(Guid.NewGuid(), prepareStep.DisplayName, nameof(ManagementScriptStep));
                        prepareStep.AccessToken = systemConnection.Authorization.Parameters["AccessToken"];
                        prepareStep.Condition = ExpressionManager.Succeeded;
                        preJobSteps.Add(prepareStep);
                    }
#endif

                    // build up 3 lists of steps, pre-job, job, post-job
                    Stack<IStep> postJobStepsBuilder = new Stack<IStep>();
                    Dictionary<Guid, Variables> taskVariablesMapping = new Dictionary<Guid, Variables>();
                    foreach (var task in message.Steps.Where(x => x.Type == Pipelines.StepType.Task).OfType<Pipelines.TaskStep>())
                    {
                        var taskDefinition = taskManager.Load(task);

                        List<string> warnings;
                        taskVariablesMapping[task.Id] = new Variables(HostContext, new Dictionary<string, VariableValue>(), out warnings);

                        // Add pre-job steps from Tasks
                        if (taskDefinition.Data?.PreJobExecution != null)
                        {
                            Trace.Info($"Adding Pre-Job {task.DisplayName}.");
                            var taskRunner = HostContext.CreateService<ITaskRunner>();
                            taskRunner.Task = task;
                            taskRunner.Stage = JobRunStage.PreJob;
                            taskRunner.Condition = ExpressionManager.Succeeded;
                            preJobSteps.Add(taskRunner);
                        }

                        // Add execution steps from Tasks
                        if (taskDefinition.Data?.Execution != null)
                        {
                            Trace.Verbose($"Adding {task.DisplayName}.");
                            var taskRunner = HostContext.CreateService<ITaskRunner>();
                            taskRunner.Task = task;
                            taskRunner.Stage = JobRunStage.Main;
                            taskRunner.Condition = taskConditionMap[task.Id];
                            jobSteps.Add(taskRunner);
                        }

                        // Add post-job steps from Tasks
                        if (taskDefinition.Data?.PostJobExecution != null)
                        {
                            Trace.Verbose($"Adding Post-Job {task.DisplayName}.");
                            var taskRunner = HostContext.CreateService<ITaskRunner>();
                            taskRunner.Task = task;
                            taskRunner.Stage = JobRunStage.PostJob;
                            taskRunner.Condition = ExpressionManager.Always;
                            postJobStepsBuilder.Push(taskRunner);
                        }
                    }

                    if (context.Container != null)
                    {
                        var containerProvider = HostContext.GetService<IContainerOperationProvider>();
                        preJobSteps.Insert(0, containerProvider.GetContainerStartStep(jobContext));
                    }

                    // create task execution context for all pre-job steps from task
                    foreach (var step in preJobSteps)
                    {
#if OS_WINDOWS
                        if (step is ManagementScriptStep)
                        {
                            continue;
                        }
#endif

                        if (step is JobExtensionRunner)
                        {
                            continue;
                        }

                        ITaskRunner taskStep = step as ITaskRunner;
                        ArgUtil.NotNull(taskStep, step.DisplayName);
                        taskStep.ExecutionContext = jobContext.CreateChild(Guid.NewGuid(), StringUtil.Loc("PreJob", taskStep.DisplayName), taskStep.Task.Name, taskVariablesMapping[taskStep.Task.Id]);
                    }

                    // Add pre-job step from Extension
                    Trace.Info("Adding pre-job step from extension.");
                    var extensionPreJobStep = GetExtensionPreJobStep(jobContext);
                    if (extensionPreJobStep != null)
                    {
                        preJobSteps.Add(extensionPreJobStep);
                    }

                    // create task execution context for all job steps from task
                    foreach (var step in jobSteps)
                    {
                        ITaskRunner taskStep = step as ITaskRunner;
                        ArgUtil.NotNull(taskStep, step.DisplayName);
                        taskStep.ExecutionContext = jobContext.CreateChild(taskStep.Task.Id, taskStep.DisplayName, taskStep.Task.Name, taskVariablesMapping[taskStep.Task.Id]);
                    }

                    // Add post-job steps from Tasks
                    Trace.Info("Adding post-job steps from tasks.");
                    while (postJobStepsBuilder.Count > 0)
                    {
                        postJobSteps.Add(postJobStepsBuilder.Pop());
                    }

                    // create task execution context for all post-job steps from task
                    foreach (var step in postJobSteps)
                    {
                        ITaskRunner taskStep = step as ITaskRunner;
                        ArgUtil.NotNull(taskStep, step.DisplayName);
                        taskStep.ExecutionContext = jobContext.CreateChild(Guid.NewGuid(), StringUtil.Loc("PostJob", taskStep.DisplayName), taskStep.Task.Name, taskVariablesMapping[taskStep.Task.Id]);
                    }

                    // Add post-job step from Extension
                    Trace.Info("Adding post-job step from extension.");
                    var extensionPostJobStep = GetExtensionPostJobStep(jobContext);
                    if (extensionPostJobStep != null)
                    {
                        postJobSteps.Add(extensionPostJobStep);
                    }

                    if (context.Container != null)
                    {
                        var containerProvider = HostContext.GetService<IContainerOperationProvider>();
                        postJobSteps.Add(containerProvider.GetContainerStopStep(jobContext));
                    }

#if OS_WINDOWS
                    // Add script post steps.
                    // This is for internal testing and is not publicly supported. This will be removed from the agent at a later time.
                    var finallyScript = Environment.GetEnvironmentVariable("VSTS_AGENT_CLEANUP_INTERNAL_TEMP_HACK");
                    if (!string.IsNullOrEmpty(finallyScript) && context.Container == null)
                    {
                        var finallyStep = new ManagementScriptStep(
                            scriptPath: finallyScript,
                            condition: ExpressionManager.Always,
                            displayName: "Agent Cleanup");

                        Trace.Verbose($"Adding agent cleanup script step.");
                        finallyStep.Initialize(HostContext);
                        finallyStep.ExecutionContext = jobContext.CreateChild(Guid.NewGuid(), finallyStep.DisplayName, nameof(ManagementScriptStep));
                        finallyStep.Condition = ExpressionManager.Always;
                        finallyStep.AccessToken = systemConnection.Authorization.Parameters["AccessToken"];
                        postJobSteps.Add(finallyStep);
                    }
#endif
                    List<IStep> result = new List<IStep>();
                    result.AddRange(preJobSteps);
                    result.AddRange(jobSteps);
                    result.AddRange(postJobSteps);

                    return result;
                }
                catch (OperationCanceledException ex) when (jobContext.CancellationToken.IsCancellationRequested)
                {
                    // Log the exception and cancel the JobExtension Initialization.
                    Trace.Error($"Caught cancellation exception from JobExtension Initialization: {ex}");
                    context.Error(ex);
                    context.Result = TaskResult.Canceled;
                    throw;
                }
                catch (Exception ex)
                {
                    // Log the error and fail the JobExtension Initialization.
                    Trace.Error($"Caught exception from JobExtension Initialization: {ex}");
                    context.Error(ex);
                    context.Result = TaskResult.Failed;
                    throw;
                }
                finally
                {
                    context.Section(StringUtil.Loc("StepFinishing", StringUtil.Loc("InitializeJob")));
                    context.Complete();
                }
            }
        }
    }
}