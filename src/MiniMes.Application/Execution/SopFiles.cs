using MediatR;

namespace MiniMes.Application.Execution;

// [v2] Spec M3 2.8b: the kiosk shows the work instruction (SOP) of its step. WorkOrderOperation.SopUrl holds a
// relative URL ("/sop/PCB-A/SMT-v1.pdf"); the files live in a read-only folder of the server.
public sealed record SopFile(Stream Content, string ContentType);

public interface ISopFileStore
{
    // null = no such file, or a path that leaves the SOP folder ("../appsettings.json").
    SopFile? Open(string relativePath);
}

public sealed record GetSopFileQuery(string Path) : IRequest<SopFile>;

public sealed class GetSopFileHandler(ISopFileStore store) : IRequestHandler<GetSopFileQuery, SopFile>
{
    public Task<SopFile> Handle(GetSopFileQuery q, CancellationToken ct)
        => Task.FromResult(store.Open(q.Path) ?? throw new KeyNotFoundException($"SOP '{q.Path}' not found."));
}
