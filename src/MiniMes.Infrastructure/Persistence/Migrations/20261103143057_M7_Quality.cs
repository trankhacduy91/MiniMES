using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M7_Quality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "NcrSeq");

            migrationBuilder.AddColumn<string>(
                name: "StatusBeforeHold",
                table: "Serials",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "StatusBeforeHold",
                table: "MaterialLots",
                type: "tinyint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FaiTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ItemsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaiTemplates", x => x.Id);
                    table.CheckConstraint("CK_FaiTemplates_ItemsJson", "ISJSON([ItemsJson]) = 1");
                    table.ForeignKey(
                        name: "FK_FaiTemplates_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InspectionPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: true),
                    OperationCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Target = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Usl = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Lsl = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    SampleSize = table.Column<int>(type: "int", nullable: false),
                    Frequency = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DefectCodeId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionPlans", x => x.Id);
                    table.CheckConstraint("CK_InspectionPlans_Limits", "([Usl] IS NOT NULL OR [Lsl] IS NOT NULL) AND ([Lsl] < [Usl] OR [Lsl] IS NULL OR [Usl] IS NULL)");
                    table.CheckConstraint("CK_InspectionPlans_Target", "([ItemId] IS NULL AND [OperationCode] IS NULL) OR ([ItemId] IS NOT NULL AND [OperationCode] IS NOT NULL AND [DefectCodeId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_InspectionPlans_DefectCodes_DefectCodeId",
                        column: x => x.DefectCodeId,
                        principalTable: "DefectCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InspectionPlans_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Ncrs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NcrNo = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    SourceRef = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    DefectCodeId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Qty = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Disposition = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    DispositionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConcessionRef = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    CapaReference = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ReviewedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ProposedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ProposedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ncrs", x => x.Id);
                    table.CheckConstraint("CK_Ncrs_Qty", "[Qty] >= 1");
                    table.ForeignKey(
                        name: "FK_Ncrs_DefectCodes_DefectCodeId",
                        column: x => x.DefectCodeId,
                        principalTable: "DefectCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FaiRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    SerialId = table.Column<long>(type: "bigint", nullable: false),
                    FaiTemplateId = table.Column<int>(type: "int", nullable: false),
                    ChecklistJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AutoChecksPassed = table.Column<bool>(type: "bit", nullable: false),
                    Result = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InspectedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    InspectedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaiRecords", x => x.Id);
                    table.CheckConstraint("CK_FaiRecords_ChecklistJson", "ISJSON([ChecklistJson]) = 1");
                    table.CheckConstraint("CK_FaiRecords_PassNeedsAuto", "[Result] <> 'Pass' OR [AutoChecksPassed] = 1");
                    table.ForeignKey(
                        name: "FK_FaiRecords_FaiTemplates_FaiTemplateId",
                        column: x => x.FaiTemplateId,
                        principalTable: "FaiTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FaiRecords_Serials_SerialId",
                        column: x => x.SerialId,
                        principalTable: "Serials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FaiRecords_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ControlLimitSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InspectionPlanId = table.Column<int>(type: "int", nullable: false),
                    Mean = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Sigma = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Ucl = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Lcl = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    MrBar = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    UclMr = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    SampleCount = table.Column<int>(type: "int", nullable: false),
                    BaselineFrom = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BaselineTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CalculatedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    CalculatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlLimitSets", x => x.Id);
                    table.CheckConstraint("CK_ControlLimitSets_Sigma", "[Sigma] > 0");
                    table.ForeignKey(
                        name: "FK_ControlLimitSets_InspectionPlans_InspectionPlanId",
                        column: x => x.InspectionPlanId,
                        principalTable: "InspectionPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Measurements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InspectionPlanId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    SerialId = table.Column<long>(type: "bigint", nullable: true),
                    StationId = table.Column<int>(type: "int", nullable: true),
                    MachineTagId = table.Column<int>(type: "int", nullable: true),
                    Value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Usl = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Lsl = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Result = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                    MeasuredAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    SourceTs = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    MeasuredBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ClientRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrectsMeasurementId = table.Column<long>(type: "bigint", nullable: true),
                    CorrectionReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Measurements", x => x.Id);
                    table.CheckConstraint("CK_Measurements_Correction", "([CorrectsMeasurementId] IS NULL AND [CorrectionReason] IS NULL) OR ([CorrectsMeasurementId] IS NOT NULL AND [CorrectionReason] IS NOT NULL)");
                    table.CheckConstraint("CK_Measurements_OneSource", "([Source] = 'Machine' AND [MachineTagId] IS NOT NULL AND [SourceTs] IS NOT NULL AND [SerialId] IS NULL AND [StationId] IS NULL) OR ([Source] <> 'Machine' AND [SerialId] IS NOT NULL AND [StationId] IS NOT NULL AND [MachineTagId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_Measurements_InspectionPlans_InspectionPlanId",
                        column: x => x.InspectionPlanId,
                        principalTable: "InspectionPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Measurements_MachineTags_MachineTagId",
                        column: x => x.MachineTagId,
                        principalTable: "MachineTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Measurements_Measurements_CorrectsMeasurementId",
                        column: x => x.CorrectsMeasurementId,
                        principalTable: "Measurements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Measurements_Serials_SerialId",
                        column: x => x.SerialId,
                        principalTable: "Serials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Measurements_Stations_StationId",
                        column: x => x.StationId,
                        principalTable: "Stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HoldRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TargetType = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    TargetId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    ScopeRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NcrId = table.Column<int>(type: "int", nullable: true),
                    HeldBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    HeldAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReleasedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleaseNote = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoldRecords", x => x.Id);
                    table.CheckConstraint("CK_HoldRecords_Release", "([ReleasedAt] IS NULL AND [ReleasedBy] IS NULL AND [ReleaseNote] IS NULL) OR ([ReleasedAt] IS NOT NULL AND [ReleasedBy] IS NOT NULL AND [ReleaseNote] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_HoldRecords_Ncrs_NcrId",
                        column: x => x.NcrId,
                        principalTable: "Ncrs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NcrItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NcrId = table.Column<int>(type: "int", nullable: false),
                    TargetType = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    TargetId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NcrItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NcrItems_Ncrs_NcrId",
                        column: x => x.NcrId,
                        principalTable: "Ncrs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpcAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InspectionPlanId = table.Column<int>(type: "int", nullable: false),
                    RuleCode = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    MeasurementId = table.Column<long>(type: "bigint", nullable: true),
                    RaisedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    NcrId = table.Column<int>(type: "int", nullable: true),
                    ClosedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CloseNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpcAlerts", x => x.Id);
                    table.CheckConstraint("CK_SpcAlerts_Point", "[MeasurementId] IS NOT NULL OR [RuleCode] = 'DataGap'");
                    table.ForeignKey(
                        name: "FK_SpcAlerts_InspectionPlans_InspectionPlanId",
                        column: x => x.InspectionPlanId,
                        principalTable: "InspectionPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SpcAlerts_Measurements_MeasurementId",
                        column: x => x.MeasurementId,
                        principalTable: "Measurements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SpcAlerts_Ncrs_NcrId",
                        column: x => x.NcrId,
                        principalTable: "Ncrs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MachineTags_SpcCharacteristicId",
                table: "MachineTags",
                column: "SpcCharacteristicId");

            migrationBuilder.CreateIndex(
                name: "UX_ControlLimitSets_ActivePerPlan",
                table: "ControlLimitSets",
                column: "InspectionPlanId",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_FaiRecords_FaiTemplateId",
                table: "FaiRecords",
                column: "FaiTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_FaiRecords_SerialId",
                table: "FaiRecords",
                column: "SerialId");

            migrationBuilder.CreateIndex(
                name: "IX_FaiRecords_WorkOrderId_InspectedAt",
                table: "FaiRecords",
                columns: new[] { "WorkOrderId", "InspectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FaiTemplates_ItemId_Version",
                table: "FaiTemplates",
                columns: new[] { "ItemId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_FaiTemplates_ActivePerItem",
                table: "FaiTemplates",
                column: "ItemId",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_HoldRecords_ActiveTarget",
                table: "HoldRecords",
                columns: new[] { "TargetType", "TargetId" },
                filter: "[ReleasedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HoldRecords_BatchId_TargetType_TargetId",
                table: "HoldRecords",
                columns: new[] { "BatchId", "TargetType", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HoldRecords_NcrId",
                table: "HoldRecords",
                column: "NcrId",
                filter: "[NcrId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionPlans_Code",
                table: "InspectionPlans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InspectionPlans_DefectCodeId",
                table: "InspectionPlans",
                column: "DefectCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionPlans_ItemOperation",
                table: "InspectionPlans",
                columns: new[] { "ItemId", "OperationCode" },
                filter: "[IsActive] = 1 AND [ItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_CorrectsMeasurementId",
                table: "Measurements",
                column: "CorrectsMeasurementId",
                filter: "[CorrectsMeasurementId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_Plan_MeasuredAt",
                table: "Measurements",
                columns: new[] { "InspectionPlanId", "MeasuredAt", "Id" },
                descending: new[] { false, true, true })
                .Annotation("SqlServer:Include", new[] { "Value", "SerialId", "Result" });

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_SerialId",
                table: "Measurements",
                column: "SerialId",
                filter: "[SerialId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_StationId",
                table: "Measurements",
                column: "StationId");

            migrationBuilder.CreateIndex(
                name: "UX_Measurements_ClientRequestId",
                table: "Measurements",
                column: "ClientRequestId",
                unique: true,
                filter: "[ClientRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Measurements_TagSourceTs",
                table: "Measurements",
                columns: new[] { "MachineTagId", "SourceTs" },
                unique: true,
                filter: "[MachineTagId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NcrItems_NcrId_TargetType_TargetId",
                table: "NcrItems",
                columns: new[] { "NcrId", "TargetType", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NcrItems_TargetType_TargetId",
                table: "NcrItems",
                columns: new[] { "TargetType", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Ncrs_DefectCodeId",
                table: "Ncrs",
                column: "DefectCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Ncrs_NcrNo",
                table: "Ncrs",
                column: "NcrNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ncrs_Status_CreatedAt",
                table: "Ncrs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SpcAlerts_InspectionPlanId_RaisedAt",
                table: "SpcAlerts",
                columns: new[] { "InspectionPlanId", "RaisedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SpcAlerts_MeasurementId",
                table: "SpcAlerts",
                column: "MeasurementId");

            migrationBuilder.CreateIndex(
                name: "IX_SpcAlerts_NcrId",
                table: "SpcAlerts",
                column: "NcrId");

            migrationBuilder.CreateIndex(
                name: "UX_SpcAlerts_OpenPerRule",
                table: "SpcAlerts",
                columns: new[] { "InspectionPlanId", "RuleCode" },
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.AddForeignKey(
                name: "FK_MachineTags_InspectionPlans_SpcCharacteristicId",
                table: "MachineTags",
                column: "SpcCharacteristicId",
                principalTable: "InspectionPlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Q16 / 2.5b: measurements and FAI judgements are evidence, insert-only like ScanLogs (ADR-004). A wrong
            // value gets a correction row pointing at it.
            foreach (var table in InsertOnlyTables)
                migrationBuilder.Sql($"""
                    CREATE TRIGGER dbo.TR_{table}_BlockModify ON dbo.{table}
                    INSTEAD OF UPDATE, DELETE
                    AS
                    BEGIN
                        SET NOCOUNT ON;
                        THROW 51000, '{table} is append-only. UPDATE/DELETE is not allowed.', 1;
                    END;
                    """);

            // 2.6: a hold is history. Releasing fills the three release columns once; linking the NCR later is
            // allowed (hold first, NCR after: containment). Nothing else changes and nothing is deleted.
            migrationBuilder.Sql("""
                CREATE TRIGGER dbo.TR_HoldRecords_ReleaseOnly ON dbo.HoldRecords
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
                        THROW 51000, 'HoldRecords cannot be deleted: release the hold instead.', 1;
                    IF UPDATE(TargetType) OR UPDATE(TargetId) OR UPDATE(BatchId) OR UPDATE(Scope) OR UPDATE(ScopeRef)
                       OR UPDATE(Reason) OR UPDATE(HeldBy) OR UPDATE(HeldAt)
                        THROW 51000, 'HoldRecords: only the release columns and NcrId can be updated.', 1;
                    IF EXISTS (SELECT 1 FROM deleted WHERE ReleasedAt IS NOT NULL)
                        THROW 51000, 'HoldRecords: a released hold cannot be changed.', 1;
                END;
                """);
        }

        private static readonly string[] InsertOnlyTables = ["Measurements", "FaiRecords"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_HoldRecords_ReleaseOnly;");
            foreach (var table in InsertOnlyTables)
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS dbo.TR_{table}_BlockModify;");

            migrationBuilder.DropForeignKey(
                name: "FK_MachineTags_InspectionPlans_SpcCharacteristicId",
                table: "MachineTags");

            migrationBuilder.DropTable(
                name: "ControlLimitSets");

            migrationBuilder.DropTable(
                name: "FaiRecords");

            migrationBuilder.DropTable(
                name: "HoldRecords");

            migrationBuilder.DropTable(
                name: "NcrItems");

            migrationBuilder.DropTable(
                name: "SpcAlerts");

            migrationBuilder.DropTable(
                name: "FaiTemplates");

            migrationBuilder.DropTable(
                name: "Measurements");

            migrationBuilder.DropTable(
                name: "Ncrs");

            migrationBuilder.DropTable(
                name: "InspectionPlans");

            migrationBuilder.DropIndex(
                name: "IX_MachineTags_SpcCharacteristicId",
                table: "MachineTags");

            migrationBuilder.DropColumn(
                name: "StatusBeforeHold",
                table: "Serials");

            migrationBuilder.DropColumn(
                name: "StatusBeforeHold",
                table: "MaterialLots");

            migrationBuilder.DropSequence(
                name: "NcrSeq");
        }
    }
}
