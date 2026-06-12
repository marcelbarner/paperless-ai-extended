using System.Threading.Channels;
using PaperlessAI.API.Models.Domain;

namespace PaperlessAI.API.Queue;

public class DocumentProcessingChannel
{
    private readonly Channel<ProcessingJob> _channel = Channel.CreateUnbounded<ProcessingJob>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelWriter<ProcessingJob> Writer => _channel.Writer;
    public ChannelReader<ProcessingJob> Reader => _channel.Reader;
}
