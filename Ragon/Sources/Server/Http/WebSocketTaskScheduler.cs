using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog.LayoutRenderers.Wrappers;

namespace Ragon.Core;

public class WebSocketTaskScheduler: TaskScheduler
{
  private ChannelReader<Task> _reader;
  private ChannelWriter<Task> _writer;
  private Queue<Task> _pendingTasks;
  
  public WebSocketTaskScheduler()
  {
    var channel = Channel.CreateUnbounded<Task>();
    _pendingTasks = new Queue<Task>();
    _reader = channel.Reader;
    _writer = channel.Writer;
  }

  protected override IEnumerable<Task>? GetScheduledTasks()
  {
    throw new NotSupportedException();
  }

  protected override void QueueTask(Task task)
  {
    _writer.TryWrite(task);
  }

  protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
  {
    return false;
  }

  public void Process()
  {
    while (_reader.TryRead(out var task))
    {
      TryExecuteTask(task);
      
      if (task.Status != TaskStatus.RanToCompletion)
        _pendingTasks.Enqueue(task);
    }

    while (_pendingTasks.TryDequeue(out var task))
      _writer.TryWrite(task);
  }
}