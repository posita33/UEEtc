using UEEtc.Blueprint;

namespace UEEtc.Blueprint.Cpp;

public sealed record ConversionDiagnostic(string Severity, string Graph, string Node, string Message);
public sealed record CppConversionOutput(string ClassName, string Header, string Implementation,
    int FunctionCount, IReadOnlyList<ConversionDiagnostic> Diagnostics)
{
    public bool IsComplete => Diagnostics.All(d => d.Severity != "Error");
}
public sealed record BlueprintCppResult(BlueprintAnalysisResult Analysis, string Directory, CppConversionOutput Code);
public interface IBlueprintCppConversionService
{
    Task<BlueprintCppResult> ConvertAsync(BlueprintAnalysisOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
