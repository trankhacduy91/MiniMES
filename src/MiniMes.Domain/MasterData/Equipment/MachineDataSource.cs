namespace MiniMes.Domain.MasterData.Equipment;

// [v2] How the machine talks on the shop floor. Display/troubleshooting only: every machine reaches
// the MES over MQTT (OPC UA and Modbus machines go through the Edge Gateway, M4b).
public enum MachineDataSource
{
    Mqtt,
    OpcUa,
    Modbus,
}
