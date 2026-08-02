using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Harness;

/// <summary>
/// Bounds the grounding tool loop, and — just as usefully — COUNTS it.
///
/// <para>
/// Nothing bounded it before. Semantic Kernel falls back to
/// <c>DefaultMaximumAutoInvokeAttempts = 128</c> when a caller sets no maximum, so the only real
/// ceiling on grounding was the streaming deadline: the model could ask for a hundred reads and
/// the phase would simply take as long as that took.
/// </para>
///
/// <para>
/// THE COUNT IS THE POINT. Grounding was the phase that varied from 80s to 240s, and the number
/// that varied was never recorded anywhere — <c>Trace.ToolCallCount</c> gives the tool total but
/// not how many ROUNDS the model needed, and the two differ whenever it batches. A phase whose
/// cost is "however many times it decides to ask" should say how many times it asked.
/// </para>
///
/// <para>
/// The cap is a backstop, not a plan. A meal goal settles in six or seven rounds; the limit sits
/// well above that so it never shapes a normal run, and when it does fire it is logged loudly
/// rather than silently truncating the world the planner is about to reason over. Terminating is
/// the right failure: the model keeps whatever it has already read, and compose plans on a
/// smaller but honest picture instead of the phase running until the deadline kills it outright.
/// </para>
/// </summary>
public sealed class ToolRoundFilter : IAutoFunctionInvocationFilter
{
    /// <summary>
    /// Rounds allowed before the loop is cut short. Measured runs use 6-9 tool calls across
    /// fewer rounds, so this is roughly triple the observed need.
    /// </summary>
    public const int MaxRounds = 24;

    private readonly ILogger<ToolRoundFilter> _logger;

    public ToolRoundFilter(ILogger<ToolRoundFilter> logger) => _logger = logger;

    /// <summary>Rounds used by the most recent grounding pass. Reset per goal.</summary>
    public int Rounds { get; private set; }

    /// <summary>Highest round count seen since the process started — the tail, at a glance.</summary>
    public int PeakRounds { get; private set; }

    public void BeginGoal()
    {
        Rounds = 0;
    }

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        // Iteration is 0-based and counts MODEL turns, not individual functions: several
        // parallel calls in one assistant turn share an iteration. That is the number worth
        // bounding, because it is the number of round-trips.
        Rounds = Math.Max(Rounds, context.RequestSequenceIndex + 1);
        PeakRounds = Math.Max(PeakRounds, Rounds);

        await next(context);

        if (context.RequestSequenceIndex + 1 >= MaxRounds)
        {
            _logger.LogWarning(
                "tool_rounds_capped rounds={Rounds} max={Max} function={Function} — ending the grounding loop; compose will plan on what was read so far",
                context.RequestSequenceIndex + 1, MaxRounds, context.Function.Name);
            context.Terminate = true;
        }
    }
}
