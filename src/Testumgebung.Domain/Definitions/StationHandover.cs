namespace Testumgebung.Domain.Definitions;

public interface IStationHandover
{
    Task WaitAsync(int side, string channel, int firstSide, CancellationToken cancellationToken);
    void Release(int side, string channel);
}

public sealed record PairedStationContext(IStationHandover Handover, int Side);
