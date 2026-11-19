using System.Windows.Input;

namespace LocalMind.AgentHub;

/// <summary>One workspace action. Menus, buttons and shortcuts all bind to the same instance.</summary>
public sealed class WorkspaceCommand(Func<object?, bool> canRun, Func<object?, Task> run) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public void Changed() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public bool CanExecute(object? parameter) => canRun(parameter);
    public void Execute(object? parameter) => _ = RunAsync(parameter);

    /// <summary>Awaitable entry point; a disabled command does nothing.</summary>
    public Task RunAsync(object? parameter = null) => canRun(parameter) ? run(parameter) : Task.CompletedTask;
}
