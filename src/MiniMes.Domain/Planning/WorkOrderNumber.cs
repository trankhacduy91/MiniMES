using MiniMes.Domain.Common;

namespace MiniMes.Domain.Planning;

// WO-yyMM-#####, e.g. WO-2610-00001. The month is only for reading; uniqueness comes from the global
// WoNoSeq sequence (never reset), so numbers keep growing across months and never repeat.
public static class WorkOrderNumber
{
    public static string Format(DateOnly plantDate, long sequence)
    {
        if (sequence < 1) throw new DomainException("Work order sequence must be at least 1.");
        var number = $"WO-{plantDate:yyMM}-{sequence:D5}";   // past 99999 it simply gets a 6th digit
        if (number.Length > WorkOrder.WoNoMaxLength) throw new DomainException("Work order sequence out of range.");
        return number;
    }
}
