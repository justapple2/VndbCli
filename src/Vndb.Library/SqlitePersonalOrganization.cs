using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static PersonalOrganizationState ReadPersonalOrganization(SqliteCommand command)
    {
        var value = command.ExecuteScalar() is string json ? Read<PersonalOrganizationState>(json) : PersonalOrganizationRules.Empty;
        PersonalOrganizationRules.Validate(value); return value;
    }
    public PersonalOrganizationState ReadPersonalOrganization()
    {
        using var db = Open(); using var command = Command(db, null, "SELECT record_json FROM personal_organization WHERE id=1");
        return ReadPersonalOrganization(command);
    }
    public PersonalOrganizationState SavePersonalOrganization(PersonalOrganizationChange change, string key) =>
        Mutate("personal-organization", key, change, (db, tx) =>
        {
            if (WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(change, VndbJson.Options))) throw new ArgumentException("个人分类不能包含本机路径。");
            using var command = Command(db, tx, "SELECT record_json FROM personal_organization WHERE id=1");
            var before = ReadPersonalOrganization(command); PersonalOrganizationState result;
            try { result = PersonalOrganizationRules.Apply(before, change); }
            catch (PersonalOrganizationConflictException e) { throw new PlayConflictException(e.Message); }
            foreach (var member in change.Items ?? []) if (Item(db, tx, member.ItemId).DeletedAt is not null) throw new PlayConflictException("请先恢复所选书架条目。");
            var json = Json(result);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024) throw new ArgumentException("个人分类超过4MiB预算。");
            Execute(db, tx, "INSERT INTO personal_organization VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET record_json=excluded.record_json", ("$json", json));
            Event(db, tx, "personal-organization", "personal-organization-edit", Json(before), json); return result;
        });
}
