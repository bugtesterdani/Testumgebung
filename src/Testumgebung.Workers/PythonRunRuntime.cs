using Testumgebung.Domain.Code;
using Testumgebung.Contracts;

namespace Testumgebung.Workers;

/// <summary>Leitet nur Python an die Umgebung des aktuellen Prüfablaufs weiter.</summary>
internal sealed class PythonRunRuntime(ICodeRuntime parent, ICodeRuntime python) : ICodeRuntime
{
    private ICodeRuntime For(CodeLanguage language) => language == CodeLanguage.Python ? python : parent;
    public Task<string> GetRuntimeInfoAsync(CodeLanguage language, CancellationToken cancellationToken)
        => For(language).GetRuntimeInfoAsync(language, cancellationToken);
    public Task<CompileResult> CompileAsync(CodeRequest request, CancellationToken cancellationToken)
        => For(request.Language).CompileAsync(request, cancellationToken);
    public Task<CodeResult> InvokeAsync(CodeRequest request, IReadOnlyDictionary<string, object?> values,
        ICodeHost host, TimeSpan abortTimeout, CancellationToken cancellationToken)
        => For(request.Language).InvokeAsync(request, values, host, abortTimeout, cancellationToken);
}
