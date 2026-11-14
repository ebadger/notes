namespace Notes.Api.Authorization;

public enum AccessLevel
{
    None,
    Read,
    Edit,
    Owner
}

public enum NoteOperation
{
    Read,
    EditContent,
    ManageSharing,
    Delete
}

public static class AccessPolicy
{
    public static bool Can(AccessLevel access, NoteOperation operation) => operation switch
    {
        NoteOperation.Read => access is AccessLevel.Read or AccessLevel.Edit or AccessLevel.Owner,
        NoteOperation.EditContent => access is AccessLevel.Edit or AccessLevel.Owner,
        NoteOperation.ManageSharing or NoteOperation.Delete => access == AccessLevel.Owner,
        _ => false
    };
}
