using MiniMes.Domain.Common;
using MiniMes.Domain.Planning;

namespace MiniMes.Domain.Execution;

public enum ScanResult
{
    Ok,
    Ng,
}

// Who sent the scan: an operator at a kiosk, or a machine (the M4 simulator playing an automatic tester).
public enum ScanSource
{
    Kiosk,
    Machine,
}

// One accepted scan (spec M3). Insert-only evidence for audits and the source of FPY: a mistake is corrected
// by a new row, never by editing one (the database refuses UPDATE/DELETE). Blocked scans are not stored here:
// nothing happened to the board. Not IAudited: this is the high-volume table, millions of rows.
public class ScanLog
{
    public long Id { get; private set; }
    public string SerialNo { get; private set; }        // varchar: route check looks up by it on every scan
    public long SerialId { get; private set; }
    public int WorkOrderId { get; private set; }
    public int StationId { get; private set; }
    public int Seq { get; private set; }                // routing step done; 0 at the Repair station (off-routing)
    public ScanResult Result { get; private set; }
    public string? DefectCode { get; private set; }     // required when NG: Pareto (M5/M7) groups by it
    public string OperatorCode { get; private set; }    // from the JWT, never from the request body
    public ScanSource Source { get; private set; }
    public DateTime ScannedAt { get; private set; }     // server time, UTC

    private ScanLog() { SerialNo = OperatorCode = string.Empty; }

    public ScanLog(Serial serial, int stationId, int seq, ScanResult result, string? defectCode,
        string operatorCode, ScanSource source, DateTime utcNow)
    {
        if (result == ScanResult.Ng && string.IsNullOrWhiteSpace(defectCode))
            throw new DomainException("An NG result needs a defect code.");
        if (result == ScanResult.Ok && defectCode is not null)
            throw new DomainException("An OK result has no defect code.");
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorCode);

        SerialNo = serial.SerialNo;
        SerialId = serial.Id;
        WorkOrderId = serial.WorkOrderId;
        StationId = stationId;
        Seq = seq;
        Result = result;
        DefectCode = defectCode;
        OperatorCode = operatorCode;
        Source = source;
        ScannedAt = utcNow;
    }
}
