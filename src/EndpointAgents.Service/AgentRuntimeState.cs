using EndpointAgents.Shared.Support;

namespace EndpointAgents.Service;

public sealed class AgentRuntimeState
{
    private readonly object _gate = new();
    private string _agentId = string.Empty;
    private string _version = string.Empty;
    private AgentConnectionStatus _connectionStatus = AgentConnectionStatus.Starting;
    private AgentFunctionMode _functionMode = AgentFunctionMode.Active;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastConnectedAtUtc;
    private DateTimeOffset? _lastCommandAtUtc;
    private string? _lastCommandMethod;
    private bool? _lastCommandSucceeded;
    private int _activeCommands;
    private string? _lastProblemCode;
    private string? _lastProblemMessage;
    private DateTimeOffset? _lastProblemAtUtc;

    public bool IsPaused
    {
        get { lock (_gate) return _functionMode == AgentFunctionMode.Paused; }
    }

    public void Initialize(string agentId, string version)
    {
        lock (_gate)
        {
            _agentId = agentId;
            _version = version;
        }
    }

    public void SetConnection(AgentConnectionStatus status, Exception? exception = null)
    {
        lock (_gate)
        {
            _connectionStatus = status;
            if (status == AgentConnectionStatus.Connected)
            {
                _lastConnectedAtUtc = DateTimeOffset.UtcNow;
                if (_lastProblemCode?.StartsWith("Connection", StringComparison.Ordinal) == true)
                    ClearProblemCore();
            }
            else if (exception is not null)
            {
                SetProblemCore("Connection." + exception.GetType().Name, exception.Message);
            }
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
            _functionMode = paused ? AgentFunctionMode.Paused : AgentFunctionMode.Active;
    }

    public void BeginCommand(string method)
    {
        lock (_gate)
        {
            _activeCommands++;
            _lastCommandAtUtc = DateTimeOffset.UtcNow;
            _lastCommandMethod = method;
        }
    }

    public void CompleteCommand(string method, bool succeeded, string? problemCode = null,
        string? problemMessage = null)
    {
        lock (_gate)
        {
            _activeCommands = Math.Max(0, _activeCommands - 1);
            _lastCommandAtUtc = DateTimeOffset.UtcNow;
            _lastCommandMethod = method;
            _lastCommandSucceeded = succeeded;
            if (succeeded)
            {
                if (_lastProblemCode?.StartsWith("Command", StringComparison.Ordinal) == true)
                    ClearProblemCore();
            }
            else
            {
                SetProblemCore(problemCode ?? "Command.Failed",
                    problemMessage ?? "The last endpoint function did not complete successfully.");
            }
        }
    }

    public void RecordProblem(string code, string message)
    {
        lock (_gate) SetProblemCore(code, message);
    }

    public AgentRuntimeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new AgentRuntimeSnapshot(_agentId, Environment.MachineName, _version,
                _connectionStatus, _functionMode, _startedAtUtc, _lastConnectedAtUtc,
                _lastCommandAtUtc, _lastCommandMethod, _lastCommandSucceeded, _activeCommands,
                _lastProblemCode, _lastProblemMessage, _lastProblemAtUtc);
        }
    }

    private void SetProblemCore(string code, string message)
    {
        _lastProblemCode = code;
        _lastProblemMessage = message.Length > 1_000 ? message[..1_000] : message;
        _lastProblemAtUtc = DateTimeOffset.UtcNow;
    }

    private void ClearProblemCore()
    {
        _lastProblemCode = null;
        _lastProblemMessage = null;
        _lastProblemAtUtc = null;
    }
}

