using System.Buffers;
using System.Reflection;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.Traffic;

/// <summary>
/// A SignalR hub protocol that counts every message it parses (client to Gateway) and writes (Gateway to
/// client), named by hub method, with the exact number of bytes the message occupies in the transport
/// (MessagePack's length prefix and JSON's record separator included). It wraps the real protocol and changes
/// nothing it produces or accepts.
///
/// NAMES. An invocation is named by its method - prefixed with the hub route when the message is running
/// inside that hub's own connection (<c>/director-stream PushDelta</c>). An INBOUND method name is chosen by
/// the client, so it is checked against the hubs' real methods and anything else is counted as
/// <c>(unknown method)</c>. Items of a client-to-server stream (StreamUp) carry only a stream id; the protocol
/// remembers which method opened that id on this connection and names the items after it. Completions, pings
/// and the other framework messages are named by their kind.
///
/// ACCOUNT. Read from the <see cref="TrafficScope"/> the current code runs in; see there for what that means
/// for a message the Gateway sends while serving some other request.
/// </summary>
public sealed class CountingHubProtocol : IHubProtocol
{
    private readonly IHubProtocol _inner;
    private readonly TrafficMeter _meter;
    private readonly IReadOnlyDictionary<string, string> _knownMethods;

    public CountingHubProtocol(IHubProtocol inner, TrafficMeter meter, IReadOnlyDictionary<string, string> knownMethods)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        _knownMethods = knownMethods ?? throw new ArgumentNullException(nameof(knownMethods));
    }

    public string Name => _inner.Name;
    public int Version => _inner.Version;
    public TransferFormat TransferFormat => _inner.TransferFormat;

    public bool IsVersionSupported(int version) => _inner.IsVersionSupported(version);

    public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HubMessage? message)
    {
        var before = input.Length;
        var parsed = _inner.TryParseMessage(ref input, binder, out message);
        if (parsed && message is not null)
            Count(message, before - input.Length, inbound: true);
        return parsed;
    }

    public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
    {
        var counting = new CountingBufferWriter(output);
        _inner.WriteMessage(message, counting);
        Count(message, counting.Written, inbound: false);
    }

    public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
    {
        var bytes = _inner.GetMessageBytes(message);
        Count(message, bytes.Length, inbound: false);
        return bytes;
    }

    private void Count(HubMessage message, long bytes, bool inbound)
    {
        try
        {
            var scope = TrafficScope.Current;
            var method = MethodName(message, inbound, scope);
            var name = scope?.Hub is { } hub ? hub + " " + method : method;
            var client = scope?.Hub is not null ? scope.Client : TrafficMeter.NoClient;
            _meter.Cell(inbound ? TrafficMeter.SignalRIn : TrafficMeter.SignalROut, name, client, scope?.Account)
                .AddMessage(inbound ? 0 : bytes, inbound ? bytes : 0);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CountingHubProtocol] message not counted ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private string MethodName(HubMessage message, bool inbound, TrafficScope? scope)
    {
        switch (message)
        {
            case HubMethodInvocationMessage invocation:
                var target = inbound ? Known(invocation.Target) : invocation.Target;
                if (inbound && invocation.StreamIds is { Length: > 0 } ids && scope is not null)
                    foreach (var id in ids)
                        scope.RememberStream(id, target);
                return target;
            case StreamItemMessage item:
                return (scope?.StreamMethod(item.InvocationId) is { } streamMethod ? streamMethod + " " : "") + "(stream item)";
            case CompletionMessage:
                return "(completion)";
            case PingMessage:
                return "(ping)";
            case CloseMessage:
                return "(close)";
            default:
                return "(" + message.GetType().Name + ")";
        }
    }

    private string Known(string? target)
        => target is not null && _knownMethods.TryGetValue(target, out var canonical) ? canonical : "(unknown method)";

    /// <summary>
    /// The methods a client may invoke on these hubs: each hub's own public instance methods, keyed
    /// case-insensitively (SignalR matches method names that way) to their declared spelling.
    /// </summary>
    public static IReadOnlyDictionary<string, string> HubMethods(params Type[] hubTypes)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hub in hubTypes)
        {
            foreach (var m in hub.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;
                map.TryAdd(m.Name, m.Name);
            }
        }
        return map;
    }

    /// <summary>
    /// Replace every hub protocol registered so far with a counting wrapper around it. Call it right after
    /// the protocols are added (AddSignalR, AddMessagePackProtocol).
    /// </summary>
    public static void DecorateAll(IServiceCollection services, TrafficMeter meter, IReadOnlyDictionary<string, string> knownMethods)
    {
        var registered = services.Where(d => d.ServiceType == typeof(IHubProtocol)).ToList();
        if (registered.Count == 0)
            throw new InvalidOperationException("[CountingHubProtocol] no hub protocol is registered yet - call DecorateAll after AddSignalR");
        foreach (var descriptor in registered)
        {
            services.Remove(descriptor);
            services.Add(ServiceDescriptor.Singleton<IHubProtocol>(sp =>
                new CountingHubProtocol(Create(sp, descriptor), meter, knownMethods)));
        }
        FileLog.Write($"[CountingHubProtocol] counting {registered.Count} hub protocol(s)");
    }

    private static IHubProtocol Create(IServiceProvider sp, ServiceDescriptor d)
    {
        if (d.ImplementationInstance is IHubProtocol instance) return instance;
        if (d.ImplementationFactory is { } factory) return (IHubProtocol)factory(sp);
        if (d.ImplementationType is { } type) return (IHubProtocol)ActivatorUtilities.CreateInstance(sp, type);
        throw new InvalidOperationException($"[CountingHubProtocol] cannot construct the hub protocol registered as {d}");
    }

    private sealed class CountingBufferWriter : IBufferWriter<byte>
    {
        private readonly IBufferWriter<byte> _inner;

        public CountingBufferWriter(IBufferWriter<byte> inner) => _inner = inner;

        public long Written { get; private set; }

        public void Advance(int count)
        {
            _inner.Advance(count);
            Written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
    }
}
