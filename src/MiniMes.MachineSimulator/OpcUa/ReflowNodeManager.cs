using Opc.Ua;
using Opc.Ua.Server;

namespace MiniMes.MachineSimulator.OpcUa;

// Spec M4b 2.2: the address space of the reflow oven, Objects/Reflow/{PeakTemp, Counter, State}.
// The first namespace a server adds after its own gets index 2, hence the NodeIds of the spec: ns=2;s=Reflow.PeakTemp.
public sealed class ReflowNodeManager : CustomNodeManager2
{
    public const string NamespaceUri = "urn:minimes:simulator:reflow";

    private BaseDataVariableState? _peakTemp, _counter, _state;

    public ReflowNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, NamespaceUri) { }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            var folder = new FolderState(null)
            {
                SymbolicName = "Reflow",
                NodeId = new NodeId("Reflow", NamespaceIndex),
                BrowseName = new QualifiedName("Reflow", NamespaceIndex),
                DisplayName = "Reflow",
                TypeDefinitionId = ObjectTypeIds.FolderType,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                EventNotifier = EventNotifiers.None,
            };
            // Hang the folder under Objects: a reference both ways, the one from Objects lives in another node manager.
            folder.AddReference(ReferenceTypeIds.Organizes, isInverse: true, ObjectIds.ObjectsFolder);
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, isInverse: false, folder.NodeId));

            _peakTemp = Variable(folder, "PeakTemp", DataTypeIds.Double, 0.0, "Peak zone temperature, °C");
            _counter = Variable(folder, "Counter", DataTypeIds.UInt32, 0u, "Cumulative board counter");
            _state = Variable(folder, "State", DataTypeIds.Int32, 0, "1 Running, 2 Idle, 3 Down, 4 Setup, 5 PlannedStop");
            AddPredefinedNode(SystemContext, folder);
        }
    }

    // Called from the machine loop while the server threads read the nodes: always under the node manager's lock.
    // machineTime is the PLC's clock (a skewed machine gives skewed SourceTimestamps, as in M4).
    public void Publish(ReflowValues v, DateTime machineTime)
    {
        lock (Lock)
        {
            if (_peakTemp is null) return;   // address space not built yet
            Set(_peakTemp, v.PeakTemp, v.SensorOk ? StatusCodes.Good : StatusCodes.BadSensorFailure, machineTime);
            Set(_counter!, v.Counter, StatusCodes.Good, machineTime);
            Set(_state!, v.State, StatusCodes.Good, machineTime);
        }
    }

    // The timestamp is the moment the value changed (SourceTimestamp on the client), not the moment we looked at it:
    // a node is only touched when its value or quality differs. ClearChangeMasks is what tells the subscriptions.
    private void Set(BaseDataVariableState node, object value, StatusCode quality, DateTime ts)
    {
        if (Equals(node.Value, value) && node.StatusCode == quality) return;
        node.Value = value;
        node.StatusCode = quality;
        node.Timestamp = ts;
        node.ClearChangeMasks(SystemContext, includeChildren: false);
    }

    private BaseDataVariableState Variable(FolderState folder, string name, NodeId dataType, object initial, string description)
    {
        var node = new BaseDataVariableState(folder)
        {
            SymbolicName = name,
            NodeId = new NodeId($"Reflow.{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            Description = description,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,           // read-only: the MES never writes to the oven
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = initial,
            StatusCode = StatusCodes.BadWaitingForInitialData,
            Timestamp = DateTime.UtcNow,
        };
        folder.AddChild(node);
        return node;
    }
}

// What the oven shows on its OPC UA server. State uses the PLC's numbering: 1 Running, 2 Idle, 3 Down, 4 Setup, 5 PlannedStop.
public sealed record ReflowValues(double PeakTemp, bool SensorOk, uint Counter, int State);
