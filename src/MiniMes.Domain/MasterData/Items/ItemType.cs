namespace MiniMes.Domain.MasterData.Items;

// Stored as text: reordering the enum later must not change existing rows.
public enum ItemType
{
    Raw,      // bought from suppliers (resistor, bare PCB)
    Semi,     // made here, consumed by a later step (PCBA after SMT)
    Finished, // shipped to the customer
}
