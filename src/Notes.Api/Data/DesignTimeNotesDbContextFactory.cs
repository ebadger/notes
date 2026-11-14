using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Notes.Api.Data;

public sealed class DesignTimeNotesDbContextFactory : IDesignTimeDbContextFactory<NotesDbContext>
{
    public NotesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotesDbContext>()
            .UseNpgsql("Host=localhost;Database=notes;Username=notes")
            .Options;
        return new NotesDbContext(options);
    }
}
