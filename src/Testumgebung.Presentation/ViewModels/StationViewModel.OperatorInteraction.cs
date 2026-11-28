using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;
using Testumgebung.Domain.Storage;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Stations;
using Testumgebung.Hardware;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationViewModel
{
    // ------------------------------------------------------------------ Werkerinteraktion

    private void ShowContent(OperatorPrompt prompt)
    {
        if (prompt.DisplayMode == OperatorDisplayMode.ReplacePrevious)
        {
            foreach (var old in Contents.Where(operatorContentViewModel => operatorContentViewModel.Prompt.DisplayMode == OperatorDisplayMode.ReplacePrevious && !operatorContentViewModel.IsAwaiting).ToList())
            {
                Contents.Remove(old);
                _contentById.Remove(old.DisplayId);
            }
        }
        var vm = new OperatorContentViewModel(prompt, _operatorAudio);
        Contents.Add(vm);
        _contentById[prompt.DisplayId] = vm;
        Hint = prompt.Text;
        OnPropertyChanged(nameof(ActiveContent));
    }

    private Task<object?> AskCoreAsync(string runId, OperatorPrompt prompt, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        _ui.Post(() =>
        {
            if (tcs.Task.IsCompleted) return;
            if (!_contentById.TryGetValue(prompt.DisplayId, out var vm))
            {
                ShowContent(prompt);
                vm = _contentById[prompt.DisplayId];
            }
            vm.Await(tcs);
            tcs.Task.ContinueWith(_ => _ui.Post(() =>
            {
                vm.EndWait();
                OnPropertyChanged(nameof(ActiveContent));
            }), TaskScheduler.Default);
            OnPropertyChanged(nameof(ActiveContent));
        });
        return tcs.Task;
    }
}
