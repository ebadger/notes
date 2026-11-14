using Notes.Api.Authorization;

namespace Notes.Api.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class AccessPolicyTests
{
    [Theory]
    [InlineData(AccessLevel.None, NoteOperation.Read, false)]
    [InlineData(AccessLevel.None, NoteOperation.EditContent, false)]
    [InlineData(AccessLevel.None, NoteOperation.ManageSharing, false)]
    [InlineData(AccessLevel.None, NoteOperation.Delete, false)]
    [InlineData(AccessLevel.Read, NoteOperation.Read, true)]
    [InlineData(AccessLevel.Read, NoteOperation.EditContent, false)]
    [InlineData(AccessLevel.Read, NoteOperation.ManageSharing, false)]
    [InlineData(AccessLevel.Read, NoteOperation.Delete, false)]
    [InlineData(AccessLevel.Edit, NoteOperation.Read, true)]
    [InlineData(AccessLevel.Edit, NoteOperation.EditContent, true)]
    [InlineData(AccessLevel.Edit, NoteOperation.ManageSharing, false)]
    [InlineData(AccessLevel.Edit, NoteOperation.Delete, false)]
    [InlineData(AccessLevel.Owner, NoteOperation.Read, true)]
    [InlineData(AccessLevel.Owner, NoteOperation.EditContent, true)]
    [InlineData(AccessLevel.Owner, NoteOperation.ManageSharing, true)]
    [InlineData(AccessLevel.Owner, NoteOperation.Delete, true)]
    public void Complete_access_matrix_is_enforced(
        AccessLevel access,
        NoteOperation operation,
        bool expected)
    {
        Assert.Equal(expected, AccessPolicy.Can(access, operation));
    }

    [Theory]
    [InlineData(AccessLevel.None)]
    [InlineData(AccessLevel.Read)]
    [InlineData(AccessLevel.Edit)]
    [InlineData(AccessLevel.Owner)]
    public void Unknown_operations_are_never_authorized(AccessLevel access)
    {
        Assert.False(AccessPolicy.Can(access, (NoteOperation)999));
    }
}
