using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MiniMes.Contracts.Audit;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Items;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.MasterData;

[Collection(ApiCollection.Name)]
public class ItemsApiTests(ApiFixture api)
{
    private static CreateItemRequest Raw(string code) => new(code, "Resistor 10k 0402", "Raw", "pcs", false, true);

    [Fact]
    public async Task Admin_creates_an_item()
    {
        var admin = await api.LoginAsync("admin");
        var code = ApiFixture.Unique("RES-10K");

        var response = await admin.PostAsJsonAsync("/api/items", Raw(code));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = await response.Content.ReadFromJsonAsync<ItemDto>();
        Assert.Equal(code, item!.Code);
        Assert.Equal("Raw", item.Type);
        Assert.True(item.IsLotControlled);
        Assert.Equal($"/api/items/{item.Id}", response.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task Same_code_again_is_409()
    {
        var admin = await api.LoginAsync("admin");
        var code = ApiFixture.Unique("DUP");
        (await admin.PostAsJsonAsync("/api/items", Raw(code))).EnsureSuccessStatusCode();

        var response = await admin.PostAsJsonAsync("/api/items", Raw(code));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Raw_item_that_is_serialized_is_400_with_a_message_on_the_field()
    {
        var admin = await api.LoginAsync("admin");

        var response = await admin.PostAsJsonAsync("/api/items",
            new CreateItemRequest(ApiFixture.Unique("BAD"), "x", "Raw", "pcs", IsSerialized: true, IsLotControlled: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var errors = await ReadErrorsAsync(response);
        Assert.Equal(["A Raw item cannot be serialized."], errors["IsSerialized"]);
    }

    [Fact]
    public async Task Writes_need_admin_and_a_token()
    {
        var anonymous = await api.Anonymous().PostAsJsonAsync("/api/items", Raw(ApiFixture.Unique("X")));
        var asOperator = await (await api.LoginOperatorAsync()).PostAsJsonAsync("/api/items", Raw(ApiFixture.Unique("X")));
        var operatorCanRead = await (await api.LoginOperatorAsync()).GetAsync("/api/items");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asOperator.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operatorCanRead.StatusCode);
    }

    [Fact]
    public async Task Unknown_id_is_404()
    {
        var admin = await api.LoginAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/items/999999")).StatusCode);
    }

    [Fact]
    public async Task List_filters_by_search_and_pages()
    {
        var admin = await api.LoginAsync("admin");
        var prefix = ApiFixture.Unique("LST");
        foreach (var n in new[] { "A", "B", "C" })
            (await admin.PostAsJsonAsync("/api/items", Raw($"{prefix}-{n}"))).EnsureSuccessStatusCode();

        var page2 = await admin.GetFromJsonAsync<PagedResult<ItemDto>>($"/api/items?search={prefix}&page=2&pageSize=2");

        Assert.Equal(3, page2!.TotalCount);
        Assert.Equal([$"{prefix}-C"], page2.Items.Select(i => i.Code));
    }

    [Fact]
    public async Task Update_renames_and_writes_one_audit_row()
    {
        var admin = await api.LoginAsync("admin");
        var created = await (await admin.PostAsJsonAsync("/api/items", Raw(ApiFixture.Unique("UPD"))))
            .Content.ReadFromJsonAsync<ItemDto>();

        var updated = await (await admin.PutAsJsonAsync($"/api/items/{created!.Id}", new UpdateItemRequest("Renamed", SkipIqc: true)))
            .Content.ReadFromJsonAsync<ItemDto>();

        Assert.Equal("Renamed", updated!.Name);
        Assert.True(updated.SkipIqc);
        var audit = await admin.GetFromJsonAsync<PagedResult<AuditLogDto>>($"/api/audit-logs?tableName=Items&recordKey={created.Id}");
        var nameRow = Assert.Single(audit!.Items, r => r.ColumnName == "Name");
        Assert.Equal(("Resistor 10k 0402", "Renamed", "admin"), (nameRow.OldValue, nameRow.NewValue, nameRow.ChangedBy));
    }

    [Fact]
    public async Task Skip_iqc_on_a_finished_item_is_422()
    {
        var admin = await api.LoginAsync("admin");
        var created = await (await admin.PostAsJsonAsync("/api/items",
            new CreateItemRequest(ApiFixture.Unique("FG"), "Board", "Finished", "pcs", true, false))).Content.ReadFromJsonAsync<ItemDto>();

        var response = await admin.PutAsJsonAsync($"/api/items/{created!.Id}", new UpdateItemRequest("Board", SkipIqc: true));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Deactivated_item_stays_readable_but_leaves_the_active_list()
    {
        var admin = await api.LoginAsync("admin");
        var code = ApiFixture.Unique("OLD");
        var created = await (await admin.PostAsJsonAsync("/api/items", Raw(code))).Content.ReadFromJsonAsync<ItemDto>();

        var response = await admin.PostAsync($"/api/items/{created!.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False((await admin.GetFromJsonAsync<ItemDto>($"/api/items/{created.Id}"))!.IsActive);
        var active = await admin.GetFromJsonAsync<PagedResult<ItemDto>>($"/api/items?search={code}&isActive=true");
        Assert.Equal(0, active!.TotalCount);
    }

    [Fact]
    public async Task There_is_no_delete()
    {
        var admin = await api.LoginAsync("admin");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await admin.DeleteAsync("/api/items/1")).StatusCode);
    }

    // Two requests at the same moment: the pre-check cannot stop both, the unique index must.
    [Fact]
    public async Task Concurrent_creates_of_the_same_code_give_one_201_and_one_409()
    {
        var admin1 = await api.LoginAsync("admin");
        var admin2 = await api.LoginAsync("admin");
        var code = ApiFixture.Unique("RACE");

        var responses = await Task.WhenAll(
            admin1.PostAsJsonAsync("/api/items", Raw(code)),
            admin2.PostAsJsonAsync("/api/items", Raw(code)));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
    }

    // The race path itself, made deterministic: skip the handler's pre-check and hit the index.
    [Fact]
    public async Task Unique_index_violation_becomes_ConflictException()
    {
        var code = ApiFixture.Unique("IDX");

        await Assert.ThrowsAsync<ConflictException>(() => api.WithDbAsync(db =>
        {
            db.Items.Add(Item.Create(code, "a", ItemType.Raw, Units.Pieces, false, true));
            db.Items.Add(Item.Create(code, "b", ItemType.Raw, Units.Pieces, false, true));
            return db.SaveChangesAsync();
        }));
    }

    private static async Task<Dictionary<string, string[]>> ReadErrorsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }
}
