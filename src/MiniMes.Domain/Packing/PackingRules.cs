namespace MiniMes.Domain.Packing;

// Why a serial does not go into the carton (P2, P5). A scan the system refuses is a normal event at the kiosk: the
// handler answers 200 + this code, not an exception (same as the route check of M3).
public enum PackBlock
{
    CartonNotOpen,
    WrongItem,
    WrongWorkOrder,
    CartonFull,
}

// Why a carton does not go onto the pallet (P7).
public enum PalletizeBlock
{
    PalletNotOpen,
    WrongItem,
    CartonNotClosed,
    CartonOnAPallet,
    CartonOnHold,
    PalletFull,
}
