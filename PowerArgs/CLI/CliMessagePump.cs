﻿using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace PowerArgs.Cli
{
    /// <summary>
    /// A class that defines the exception handling contract for a ConsoleApp
    /// </summary>
    public class PumpExceptionArgs
    {
        /// <summary>
        /// Gets the exception that a handler may handle
        /// </summary>
        public Exception Exception { get; private set; }

        /// <summary>
        /// Gets or sets whether or not this exception has been handled by a handler.  Your event handler should set this to true
        /// if you want the pump to continue processing messages.  If no handler sets this to true then the pump thread will throw
        /// and the process will crash.
        /// </summary>
        public bool Handled { get; set; }

        internal PumpExceptionArgs(Exception ex)
        {
            this.Exception = ex;
            this.Handled = false;
        }
    }

    /// <summary>
    /// A class that defines a message that can be executed by the message pump
    /// </summary>
    public class PumpMessage
    {

        /// <summary>
        /// A description for this message, used for debugging
        /// </summary>
        public string Description { get; set; }

        private Action pumpAction;

        /// <summary>
        /// Creates a pump message with the given action and idempotency id.  
        /// </summary>
        /// <param name="a">The action to execute when this message is dequeued by a message pump</param>
       /// <param name="description">A description of this message that can be used for debugging purposes</param>
        public PumpMessage(Action a, string description = "Not specified")
        {
            this.Description = description;
            this.pumpAction = a;
        }

        /// <summary>
        /// Executes the action associated with this message
        /// </summary>
        internal void Execute()
        {
            if(pumpAction != null)
            {
                pumpAction();
            }
        }
    }

    /// <summary>
    /// A message that indicates a paint request that gets processed differently from other message types to reduce
    /// the number of times the message pump processes paint requests for a console app
    /// </summary>
    internal class PaintMessage : PumpMessage
    {
        public const string PaintDescription = nameof(PaintMessage);
        public PaintMessage(Action paintAction) : base(paintAction, PaintDescription) { }
    }

    /// <summary>
    /// A class that is used to manage a CLI thread in a similar way that other platforms synchronize work
    /// on a UI thread
    /// </summary>
    public class CliMessagePump : ObservableObject
    {
        private class StopPumpMessage : PumpMessage
        {
            public StopPumpMessage() : base(() => { }, description: "Stops the pump") { }
        }

        /// <summary>
        /// An event that fires when a pump message throws an exception while executing.  Handlers can mark the exception as handled
        /// if they want to keep the pump running.  If no handler is registered or no handler marks the exception as handled then the
        /// pump thread will throw and the process will crash.
        /// </summary>
        public Event<PumpExceptionArgs> PumpException { get; private set; } = new Event<PumpExceptionArgs>();

        public Event WindowResized { get; private set; } = new Event();

        /// <summary>
        /// A boolean that can be checked to see if the pump is currently running
        /// </summary>
        public bool IsRunning { get; private set; } = false;

        private List<PumpMessage> pumpMessageQueue = new List<PumpMessage>();
        private IConsoleProvider console;
        private int managedCliThreadId;
        private int lastConsoleWidth, lastConsoleHeight;
        private Deferred runDeferred;

        private FrameRateMeter frameRateMeter;
        /// <summary>
        /// Gets the current frame rate for the app
        /// </summary>
        public int FPS
        {
            get
            {
                return frameRateMeter != null ? frameRateMeter.CurrentFPS : 0;
            }
        }

        private FrameRateMeter paintRateMeter;
        /// <summary>
        /// Gets the current paint rate for the app
        /// </summary>
        public int PPS
        {
            get
            {
                return paintRateMeter != null ? paintRateMeter.CurrentFPS : 0;
            }
        }

        /// <summary>
        /// Creates a new message pump given a console to use for keyboard input
        /// </summary>
        /// <param name="console">the console to use for keyboard input</param>
        /// <param name="keyInputHandler">a key event handler to use to process keyboard input</param>
        public CliMessagePump(IConsoleProvider console)
        {
            this.console = console;
            this.lastConsoleWidth = this.console.BufferWidth;
            this.lastConsoleHeight = this.console.WindowHeight;
        }

        /// <summary>
        /// Handles key input for the message pump
        /// </summary>
        /// <param name="info"></param>
        protected virtual void HandleKeyInput(ConsoleKeyInfo info) { }

        /// <summary>
        /// Queues the given action for processing on the pump thread
        /// </summary>
        /// <param name="a">the action that will be processed in order on the pump thread</param>
        public void QueueAction(Action a)
        {
            var pumpMessage = new PumpMessage(a);
            QueueAction(pumpMessage);
        }

        /// <summary>
        /// Queues the given message for processing on the pump thread
        /// </summary>
        /// <param name="pumpMessage">the message that will be processed in order on the pump thread</param>
        public void QueueAction(PumpMessage pumpMessage)
        {
            lock (pumpMessageQueue)
            {
                pumpMessageQueue.Add(pumpMessage);
#if PROFILING
                CliProfiler.Instance.TotalMessagesQueued++;
                if(pumpMessage is PaintMessage)
                {
                    CliProfiler.Instance.PaintMessagesQueued++;
                }
#endif
            }
        }

        protected void QueueActionInFront(Action a)
        {
            var pumpMessage = new PumpMessage(a);
            QueueActionInFront(pumpMessage);
        }

        protected void QueueActionInFront(PumpMessage pumpMessage)
        {
            lock (pumpMessageQueue)
            {
                pumpMessageQueue.Insert(0, pumpMessage);
            }
        }

        private List<Timer> asyncActionTimers = new List<Timer>();
        public void QueueAsyncAction(Task t, Action<Task> action)
        {
            t.ContinueWith((tPrime) =>
            {
                QueueAction(() => { action(tPrime); });
            });
        }

        public void QueueAsyncAction<TResult>(Task<TResult> t, Action<Task<TResult>> action)
        {
            t.ContinueWith((tPrime) =>
            {
                QueueAction(()=> { action(tPrime); });
            });
        }

        /// <summary>
        /// Schedules the given action for periodic processing by the message pump
        /// </summary>
        /// <param name="a">The action to schedule for periodic processing</param>
        /// <param name="interval">the execution interval for the action</param>
        /// <returns>A timer that can be passed to ClearInterval if you want to cancel the work</returns>
        public Timer SetInterval(Action a, TimeSpan interval)
        {
            var ret = new Timer((o) =>
            {
                QueueAction(a);
            }, null, (int)interval.TotalMilliseconds, (int)interval.TotalMilliseconds);
            return ret;
        }

        /// <summary>
        /// Schedules the given action for a one time execution after the given period elapses
        /// </summary>
        /// <param name="a">The action to schedule</param>
        /// <param name="period">the period of time to wait before executing the action</param>
        /// <returns></returns>
        public Timer SetTimeout(Action a, TimeSpan period)
        {
            var ret = new Timer((o) =>
            {
                QueueAction(a);
            }, null, (int)period.TotalMilliseconds,Timeout.Infinite);
            return ret;
        }

        /// <summary>
        /// Cancels the scheduled execution of a periodic action given the timer that was provided by SetInterval.  The timer will be disposed.
        /// </summary>
        /// <param name="t">the timer given by SetInterval</param>
        public void ClearInterval(Timer t)
        {
            try
            {
                t.Change(Timeout.Infinite, Timeout.Infinite);
                t.Dispose();
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Cancels the scheduled execution of a one time action given the timer that was provided by SetTimeout.  The timer will be disposed.
        /// </summary>
        /// <param name="t">The timer given by SetTimeout</param>
        public void ClearTimeout(Timer t)
        {
            try
            {
                t.Change(Timeout.Infinite, Timeout.Infinite);
                t.Dispose();
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Starts the message pump which will begin processing messages
        /// </summary>
        /// <returns>A task that will complete when the message pump starts</returns>
        public virtual Promise Start()
        {
            if(runDeferred != null)
            {
                throw new InvalidOperationException("Already running");
            }
            IsRunning = true;
            runDeferred = Deferred.Create();
            var pumpThread = new Thread(Pump);
            pumpThread.Priority = ThreadPriority.AboveNormal;
            pumpThread.IsBackground = true;
            pumpThread.Start();
            return runDeferred.Promise;
        }

        public void Stop()
        {
            if (IsRunning)
            {
                QueueAction(new StopPumpMessage());
            }
        }

        private void Pump()
        {
            try
            {
                bool stopRequested = false;
                frameRateMeter = new FrameRateMeter();
                paintRateMeter = new FrameRateMeter();
                while (true)
                {
                    if ((lastConsoleWidth != this.console.BufferWidth || lastConsoleHeight != this.console.WindowHeight))
                    {
                        DebounceResize();
                        WindowResized.Fire();
                    }

                    bool idle = true;
                    List<PumpMessage> iterationQueue;
                    PaintMessage iterationPaintMessage = null;
                    lock (pumpMessageQueue)
                    {
                        iterationQueue = new List<PumpMessage>(pumpMessageQueue);
                        pumpMessageQueue.Clear();
                    }

                    foreach (var message in iterationQueue)
                    {
                        idle = false;
                        if (message is StopPumpMessage)
                        {
                            stopRequested = true;
                            break;
                        }
                        else if (message is PaintMessage)
                        {
                            iterationPaintMessage = message as PaintMessage;
                        }
                        else
                        {
                            TryWork(message);
                        }
                    }

                    if (iterationPaintMessage != null)
                    {
                        TryWork(iterationPaintMessage);
                        paintRateMeter.Increment();
#if PROFILING
                    CliProfiler.Instance.PaintMessagesProcessed++;
#endif
                    }

                    if (stopRequested)
                    {
                        break;
                    }

                    if (this.console.KeyAvailable)
                    {
                        idle = false;
                        var info = this.console.ReadKey(true);
                        QueueAction(() =>
                        {
                            HandleKeyInput(info);
                        });
                    }

                    frameRateMeter.Increment();
                    if (idle)
                    {
                        Thread.Sleep(0);
                    }
#if PROFILING
                else
                { 
                    CliProfiler.Instance.TotalNonIdleIterations++;
                }
#endif
                }
                runDeferred.Resolve();
            }
            catch(Exception ex)
            {
                runDeferred.Reject(ex);
            }
            finally
            {
                IsRunning = false;
                runDeferred = null;
            }
        }

        private void TryWork(PumpMessage work)
        {
            try
            {
                work.Execute();
            }
            catch (Exception ex)
            {
                PumpExceptionArgs exceptionArgs = new PumpExceptionArgs(ex);
                PumpException.Fire(exceptionArgs);
      
                if (exceptionArgs.Handled == false)
                {
                    if (ex.InnerException != null)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    }
                    else
                    {
                        ExceptionDispatchInfo.Capture(ex).Throw();
                    }
                }
            }
#if PROFILING
            CliProfiler.Instance.TotalMessagesProcessed++;
#endif
        }

        private void DebounceResize()
        {
            console.Clear();
            bool done = false;
            ActionDebouncer debouncer = new ActionDebouncer(TimeSpan.FromSeconds(.25), () =>
            {
                done = true;
            });

            debouncer.Trigger();
            while(done == false)
            {
                if(console.BufferWidth != lastConsoleWidth || console.WindowHeight != lastConsoleHeight)
                {
                    lastConsoleWidth = console.BufferWidth;
                    lastConsoleHeight = console.WindowHeight;
                    debouncer.Trigger();
                }
            }
        }
    }
}
