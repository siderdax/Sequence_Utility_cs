using Sequence;
using SequenceUtility.Sequence.SequenceEventArgs;
using System.Diagnostics;

namespace SequenceUtility.Sequence
{
    public class SequenceQueue : SequenceCore
    {
        private SequenceResult _stopResult = null;
        private readonly object _processSyncRoot = new();

        public override event EventHandler<SequenceCoreAsyncEventArgs> OnStartAsync;
        public override event EventHandler<SequenceCoreProcEventArgs> OnStarted;
        public override event EventHandler<SequenceCoreProcEventArgs> OnFinished;
        public override event EventHandler<SequenceCoreProcEventArgs> OnStopping;
        public override event EventHandler<SequenceCoreProcEventArgs> OnStopped;
        public override event EventHandler<SequenceCoreErrorEventArgs> OnErrorOccurred;

        public Queue<SequenceCore> Queue { get; init; } = new();
        public int MaxSequenceCount { get; set; }
        private List<SequenceCore> _runningSequences = new();
        private ManualResetEventSlim _sequenceMRES = new(true);
        private Queue<(SequenceCore, ManualResetEventSlim)> _holdQueue = new();

        public enum CallNextState
        {
            Normal,
            CallAtLast,
            CallAtMid,
            CallAtFull
        }
        private CallNextState _callNextState = CallNextState.Normal;
        private readonly object _nextCallerSyncRoot = new();

        public SequenceQueue(string name, int maxSequenceCount,
                             Func<SequenceCore, Dictionary<string, object>, SequenceResult> startCondition = null,
                             Func<SequenceCore, Dictionary<string, object>, SequenceResult> finishCondition = null,
                             Func<SequenceCore, Dictionary<string, object>, SequenceResult> stopProcess = null,
                             object packSyncRoot = null,
                             object payloadSyncRoot = null)
        {
            Name = name;
            StartCondition = startCondition;
            FinishCondition = finishCondition;
            StopProcess = stopProcess;
            PayloadSyncRoot = payloadSyncRoot ?? new();
            SyncRoot = packSyncRoot ?? new();
            MaxSequenceCount = maxSequenceCount;

            if (StartCondition == null)
            {
                StartCondition = (sender, e) => new SequenceResult { Success = true };
            }

            if (FinishCondition == null)
            {
                FinishCondition = (sender, e) => new SequenceResult { Success = true };
            }
        }

        public override SequenceResult Start(Dictionary<string, object> payload)
        {
            try
            {
                lock (SyncRoot)
                {

                    SequenceResult result = new()
                    {
                        Name = Name,
                        Core = this,
                        Success = false,
                        Messages = new string[0]
                    };

                    lock (_processSyncRoot)
                    {
                        Payload = payload;
                        _runningSequences.Clear();
                        _sequenceMRES.Set();

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

                        result = StartCondition(this, Payload);
                        result.Core = this;
                        result.Name = Name;

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
                            Success = true
                        }
                    ));

                    try
                    {
                        while (Queue.Count > 0)
                        {
                            SequenceCore currentSequence;

                            lock (_processSyncRoot)
                            {
                                if (StopSignal > 0)
                                {
                                    State = SequenceState.Stopped;

                                    if (result.Messages != null)
                                    {
                                        result.Messages = result.Messages.Concat(
                                            _stopResult?.Messages?.ToArray() ?? new string[0]).ToArray();
                                    }
                                    else
                                    {
                                        result.Messages = _stopResult?.Messages?.ToArray();
                                    }

                                    result.Success = _stopResult?.Success ?? false;

                                    OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                                    OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                                    return result;
                                }

                                currentSequence = Queue.Dequeue();
                                _sequenceMRES.Reset();
                                _runningSequences.Add(currentSequence);
                            }

                            currentSequence.StartAsync(payload).ContinueWith(t =>
                            {
                                lock (_processSyncRoot)
                                {
                                    _runningSequences.Remove(currentSequence);
                                }

                                result = t.Result;

                                if (!result.Success)
                                {
                                    Stop(false);
                                    _sequenceMRES.Set();
                                }
                                else
                                {
                                    ContinueNext();
                                }
                            });
                            _sequenceMRES.Wait();
                        }
                    }
                    catch (Exception ex)
                    {
                        OnErrorOccurred?.Invoke(this, new() { Error = ex });

                        result = new()
                        {
                            Name = Name,
                            Core = this,
                            Success = false,
                            Messages = new string[] { ex.ToString() }
                        };
                    }

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

                    State = SequenceState.Done;
                    OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                    return result;
                }
            }
            catch (Exception ex)
            {
                return new SequenceResult()
                {
                    Success = false,
                    Messages = new string[]
                    {
                        $"{ex}"
                    }
                };
            }
        }

        public override Task<SequenceResult> StartAsync(Dictionary<string, object> payload)
        {
            AsyncTask = Task.Run(() => Start(payload));
            OnStartAsync?.Invoke(this, new SequenceCoreAsyncEventArgs(AsyncTask));
            return AsyncTask;
        }

        public override void Stop()
        {
            Stop(true);
        }

        public void StopRemaining()
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

        private void Stop(bool clearStopResult)
        {
            lock (_processSyncRoot)
            {
                if (State == SequenceState.Stopping)
                {
                    foreach (var seq in _runningSequences)
                    {
                        seq.Stop();
                    }
                }
                else if (State != SequenceState.Running)
                {
                    State = SequenceState.Stopped;
                    StopSignal = 1;

                    if (clearStopResult)
                        _stopResult = null;
                }
                else
                {
                    StopSignal = 1;
                    State = SequenceState.Stopping;
                    OnStopping?.Invoke(this, new SequenceCoreProcEventArgs(Name, null));
                    _stopResult = StopProcess?.Invoke(this, Payload);

                    foreach (var seq in _runningSequences)
                    {
                        seq.Stop();
                    }
                }
            }
        }

        public void Enqueue(SequenceCore core)
        {
            lock (_processSyncRoot)
            {
                Queue.Enqueue(core);
            }
        }

        public SequenceUnit CreateNextCaller(SequenceCore currentSequence, SequenceCore intermission = null)
        {
            return new SequenceUnit(
                name: $"{currentSequence.Name} Next Caller",
                startCondition: (core, payload) => new SequenceResult { Success = true },
                finishCondition: (core, payload) => new SequenceResult { Success = true },
                workProcess: (core, payload) =>
                {
                    CallNext(intermission);
                    return new SequenceResult { Success = true };
                },
                stopProcess: (core, payload) =>
                {
                    intermission?.Stop();
                    return new SequenceResult { Success = false };
                },
                unitSyncRoot: _nextCallerSyncRoot,
                payloadSyncRoot: new object()
            );
        }

        public SequenceUnit CreateHolder(string name, SequenceCore intermission = null)
        {
            return new SequenceUnit(
                name: $"{name} Holder",
                startCondition: (core, payload) => new SequenceResult { Success = true },
                finishCondition: (core, payload) => new SequenceResult { Success = true },
                workProcess: (core, payload) =>
                {
                    Hold(core, intermission);
                    return new SequenceResult { Success = true };
                },
                stopProcess: (core, payload) =>
                {
                    Unhold(core);
                    return new SequenceResult { Success = false };
                },
                unitSyncRoot: new object(),
                payloadSyncRoot: new object()
            );
        }

        private void CallNext(SequenceCore intermission)
        {
            lock (_processSyncRoot)
            {
                if (_holdQueue.Count > 0)
                {
                    _callNextState = CallNextState.Normal;
                    (SequenceCore holdIntermission, ManualResetEventSlim mres) = _holdQueue.Dequeue();
                    mres.Set();
                }
                else if (_runningSequences.Count >= MaxSequenceCount)
                {
                    _callNextState = CallNextState.CallAtFull;
                }
                else if (Queue.Count == 0 || StopSignal > 0)
                {
                    _callNextState = CallNextState.CallAtLast;
                }
                else
                {
                    if (intermission != null)
                    {
                        Debug.WriteLine("CallNext Intermission");
                        var seqResult = intermission.Start(Payload);

                        if (seqResult?.Success != true)
                            throw new Exception($"Failed to run intermission: {intermission.Name}");
                    }

                    _callNextState = CallNextState.Normal;
                    _sequenceMRES.Set();
                }
            }
        }

        private void ContinueNext()
        {
            lock (_processSyncRoot)
            {
                if (_holdQueue.Count > 0)
                {
                    _callNextState = CallNextState.Normal;
                    (SequenceCore holdIntermission, ManualResetEventSlim mres) = _holdQueue.Dequeue();

                    if (holdIntermission != null)
                    {
                        Debug.WriteLine("Hold Intermission1");
                        var seqResult = holdIntermission.Start(Payload);

                        if (seqResult?.Success != true)
                            throw new Exception($"Failed to run holdIntermission: {holdIntermission.Name}");
                    }

                    mres.Set();
                    return;
                }
                else if (_runningSequences.Count >= MaxSequenceCount)
                {
                    _callNextState = CallNextState.CallAtFull;
                }
                else if (Queue.Count == 0 || StopSignal > 0)
                {
                    if (_runningSequences.Count == 0)
                    {
                        _callNextState = CallNextState.CallAtLast;
                        _sequenceMRES.Set();
                    }
                    else
                    {
                        _callNextState = CallNextState.CallAtMid;
                    }
                }
                else
                {
                    _callNextState = CallNextState.Normal;
                    _sequenceMRES.Set();
                }
            }
        }

        private void Hold(SequenceCore holdOwner, SequenceCore intermission)
        {
            ManualResetEventSlim mres = null;

            lock (_processSyncRoot)
            {
                if (holdOwner.State != SequenceState.Running)
                    return;

                switch (_callNextState)
                {
                    case CallNextState.Normal:
                        mres = new(false);
                        _holdQueue.Enqueue((intermission, mres));
                        break;
                    case CallNextState.CallAtLast:
                    case CallNextState.CallAtFull:
                        _callNextState = CallNextState.Normal;
                        break;
                    case CallNextState.CallAtMid:
                        Debug.WriteLine("Hold Intermission2");
                        intermission?.Start(Payload);
                        _callNextState = CallNextState.Normal;
                        break;
                }
            }

            mres?.Wait();
        }

        private void Unhold(SequenceCore holdOwner)
        {
            lock (_processSyncRoot)
            {
                if (_holdQueue.FirstOrDefault(x => x.Item1 == holdOwner)
                    is (SequenceCore, ManualResetEventSlim mres))
                {
                    mres.Set();
                }
            }
        }
    }
}
