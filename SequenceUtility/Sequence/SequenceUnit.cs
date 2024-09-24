using SequenceUtility.Sequence.SequenceEventArgs;

namespace Sequence
{
    public class SequenceUnit : SequenceCore
    {
        private SequenceResult _stopResult = null;
        private readonly object _processSyncRoot = new();

        public override event EventHandler<SequenceCoreAsyncEventArgs> OnStartAsync;
        public override event EventHandler<SequenceCoreProcEventArgs> OnStarted;
        public override event EventHandler<SequenceCoreProcEventArgs> OnFinished;
        public override event EventHandler<SequenceCoreProcEventArgs> OnStopping;
        public override event EventHandler<SequenceCoreProcEventArgs> OnStopped;
        public override event EventHandler<SequenceCoreErrorEventArgs> OnErrorOccurred;

        public SequenceUnit(string name,
                            Func<SequenceCore, Dictionary<string, object>, SequenceResult> startCondition,
                            Func<SequenceCore, Dictionary<string, object>, SequenceResult> finishCondition,
                            Func<SequenceCore, Dictionary<string, object>, SequenceResult> workProcess,
                            Func<SequenceCore, Dictionary<string, object>, SequenceResult> stopProcess,
                            object unitSyncRoot,
                            object payloadSyncRoot)
        {
            Name = name;
            StartCondition = startCondition;
            FinishCondition = finishCondition;
            WorkProcess = workProcess;
            StopProcess = stopProcess;
            PayloadSyncRoot = payloadSyncRoot ?? new();
            SyncRoot = unitSyncRoot ?? new();

            if (StartCondition == null)
            {
                StartCondition = (sender, e) => new SequenceResult { Success = true };
            }

            if (FinishCondition == null)
            {
                FinishCondition = (sender, e) => new SequenceResult { Success = true };
            }
        }

        public override Task<SequenceResult> StartAsync(Dictionary<string, object> payload)
        {
            AsyncTask = Task.Run(() => Start(payload));
            OnStartAsync?.Invoke(this, new SequenceCoreAsyncEventArgs(AsyncTask));
            return AsyncTask;
        }

        public override SequenceResult Start(Dictionary<string, object> payload)
        {
            lock (SyncRoot)
            {
                try
                {
                    SequenceResult result;

                    lock (_processSyncRoot)
                    {
                        Payload = payload;

                        if (State != SequenceState.Ready)
                        {
                            result = new SequenceResult
                            {
                                Name = Name,
                                Core = this,
                                Success = false,
                                Messages = new string[] { "No Ready state" }
                            };

                            State = SequenceState.Stopped;
                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            return result;
                        }

                        _stopResult = null;
                    }

                    result = StartCondition(this, Payload);
                    result.Core = this;
                    result.Name = Name;

                    lock (_processSyncRoot)
                    {
                        if (StopSignal > 0)
                        {
                            State = SequenceState.Stopped;

                            if (result.Messages != null)
                                result.Messages = result.Messages.Concat(
                                    _stopResult?.Messages?.ToArray() ?? new string[0]).ToArray();
                            else
                                result.Messages = _stopResult?.Messages?.ToArray();

                            result.Success = _stopResult?.Success ?? false;

                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            return result;
                        }
                        else if (!result.Success)
                        {
                            State = SequenceState.Stopped;
                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            return result;
                        }
                        else
                        {
                            State = SequenceState.Running;
                        }
                    }

                    OnStarted?.Invoke(this, new SequenceCoreProcEventArgs(
                        Name,
                        new SequenceResult
                        {
                            Name = Name,
                            Core = this,
                            Success = true
                        }
                    ));

                    result = WorkProcess(this, Payload);
                    result.Core = this;
                    result.Name = Name;

                    lock (_processSyncRoot)
                    {
                        if (StopSignal > 0)
                        {
                            State = SequenceState.Stopped;

                            if (result.Messages != null)
                                result.Messages = result.Messages.Concat(
                                    _stopResult?.Messages?.ToArray() ?? new string[0]).ToArray();
                            else
                                result.Messages = _stopResult?.Messages?.ToArray();

                            result.Success = _stopResult?.Success ?? false;

                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            return result;
                        }
                        else if (!result.Success)
                        {
                            State = SequenceState.Stopped;
                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            return result;
                        }
                    }

                    result = FinishCondition(this, Payload);
                    result.Core = this;
                    result.Name = Name;

                    if (!result.Success)
                    {
                        State = SequenceState.Stopped;
                        OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                        OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                        return result;
                    }
                    else
                    {
                        State = SequenceState.Done;
                    }

                    OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                    return result;
                }
                catch (Exception ex)
                {
                    OnErrorOccurred?.Invoke(this, new() { Error = ex });

                    return new SequenceResult()
                    {
                        Name = Name,
                        Core = this,
                        Success = false,
                        Messages = new string[] { ex.ToString() }
                    };
                }
            }
        }

        public override void Stop()
        {
            lock (_processSyncRoot)
            {
                if (State != SequenceState.Running)
                {
                    State = SequenceState.Stopped;
                    StopSignal = 1;
                    _stopResult = null;
                }
                else
                {
                    StopSignal = 1;
                    State = SequenceState.Stopping;
                    OnStopping?.Invoke(this, new SequenceCoreProcEventArgs(Name, null));
                    _stopResult = StopProcess?.Invoke(this, Payload);
                }
            }
        }

        public Dictionary<string, object> UpdatePayload(Dictionary<string, object> addtional)
        {
            Payload ??= new Dictionary<string, object>();

            if (addtional == null)
                return Payload;

            foreach (var item in addtional)
            {
                Payload[item.Key] = item.Value;
            }

            return Payload;
        }
    }
}
