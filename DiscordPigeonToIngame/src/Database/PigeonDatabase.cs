using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace DiscordPigeonToIngame;

public class PigeonDatabase
{
    private readonly string _connectionString;

    public PigeonDatabase(ICoreServerAPI api)
    {
        var dir = Path.Combine(GamePaths.DataPath, "ModData", api.World.SavegameIdentifier, "discordpigeontoingame");
        Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={Path.Combine(dir, "pigeons.db")};";
        InitSchema();
    }

    private void InitSchema()
    {
        using var connection = Open();
        using (var cmd = new SqliteCommand("""
            CREATE TABLE IF NOT EXISTS Pigeons (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RecipientUid TEXT NOT NULL,
                SenderUid TEXT NOT NULL,
                Title TEXT NOT NULL,
                Message TEXT NOT NULL,
                SentAt INTEGER NOT NULL DEFAULT (strftime('%s','now')),
                Delivered INTEGER NOT NULL DEFAULT 0,
                DmFallbackSent INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_recipient ON Pigeons(RecipientUid);
            CREATE INDEX IF NOT EXISTS idx_sender ON Pigeons(SenderUid);
            """, connection))
            cmd.ExecuteNonQuery();

        if (EnsureColumn(connection, "DmFallbackSent", "INTEGER NOT NULL DEFAULT 0"))
        {
            using var backfill = new SqliteCommand("UPDATE Pigeons SET DmFallbackSent = 1", connection);
            backfill.ExecuteNonQuery();
        }
    }

    private static bool EnsureColumn(SqliteConnection connection, string name, string definition)
    {
        using var check = new SqliteCommand("SELECT COUNT(*) FROM pragma_table_info('Pigeons') WHERE name = @n", connection);
        check.Parameters.AddWithValue("@n", name);
        if (check.ExecuteScalar() is long count && count > 0) return false;
        using var alter = new SqliteCommand($"ALTER TABLE Pigeons ADD COLUMN {name} {definition}", connection);
        alter.ExecuteNonQuery();
        return true;
    }

    public long Add(string recipientUid, string senderUid, string title, string message)
    {
        using var connection = Open();
        using var cmd = new SqliteCommand(
            "INSERT INTO Pigeons (RecipientUid, SenderUid, Title, Message) VALUES (@r, @s, @t, @m); SELECT last_insert_rowid();",
            connection);
        cmd.Parameters.AddWithValue("@r", recipientUid);
        cmd.Parameters.AddWithValue("@s", senderUid);
        cmd.Parameters.AddWithValue("@t", title);
        cmd.Parameters.AddWithValue("@m", message);
        return (long)cmd.ExecuteScalar();
    }

    public bool TryMarkDmFallbackSent(long id)
    {
        using var connection = Open();
        using var cmd = new SqliteCommand(
            "UPDATE Pigeons SET DmFallbackSent = 1 WHERE Id = @id AND Delivered = 0 AND DmFallbackSent = 0",
            connection);
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<(long Id, string RecipientUid, string Title, string Message, long SentAt)> GetDmFallbackPending()
    {
        using var connection = Open();
        using var cmd = new SqliteCommand(
            "SELECT Id, RecipientUid, Title, Message, SentAt FROM Pigeons WHERE Delivered = 0 AND DmFallbackSent = 0",
            connection);
        using var reader = cmd.ExecuteReader();
        var results = new List<(long, string, string, string, long)>();
        while (reader.Read())
            results.Add(((long)reader["Id"], (string)reader["RecipientUid"], (string)reader["Title"], (string)reader["Message"], (long)reader["SentAt"]));
        return results;
    }

    public List<(long Id, string SenderUid, string Title, string Message)> GetPending(string recipientUid, int delaySeconds)
    {
        using var connection = Open();
        using var cmd = new SqliteCommand(
            "SELECT Id, SenderUid, Title, Message FROM Pigeons WHERE RecipientUid = @r AND Delivered = 0 AND SentAt + @delay <= strftime('%s','now')",
            connection);
        cmd.Parameters.AddWithValue("@r", recipientUid);
        cmd.Parameters.AddWithValue("@delay", delaySeconds);
        using var reader = cmd.ExecuteReader();
        var results = new List<(long, string, string, string)>();
        while (reader.Read())
            results.Add(((long)reader["Id"], (string)reader["SenderUid"], (string)reader["Title"], (string)reader["Message"]));
        return results;
    }

    public List<(string RecipientUid, string Title, string Message, long SentAt, bool Delivered)> GetSentPigeons(string senderUid)
    {
        using var connection = Open();
        using var cmd = new SqliteCommand(
            "SELECT RecipientUid, Title, Message, SentAt, Delivered FROM Pigeons WHERE SenderUid = @s ORDER BY SentAt DESC",
            connection);
        cmd.Parameters.AddWithValue("@s", senderUid);
        using var reader = cmd.ExecuteReader();
        var results = new List<(string RecipientUid, string Title, string Message, long SentAt, bool Delivered)>();
        while (reader.Read())
            results.Add((RecipientUid: (string)reader["RecipientUid"], Title: (string)reader["Title"], Message: (string)reader["Message"], SentAt: (long)reader["SentAt"], Delivered: (long)reader["Delivered"] == 1));
        return results;
    }

    public List<(string SenderUid, string Title, string Message, long SentAt, bool Delivered)> GetReceivedPigeons(string recipientUid)
    {
        using var connection = Open();
        using var cmd = new SqliteCommand(
            "SELECT SenderUid, Title, Message, SentAt, Delivered FROM Pigeons WHERE RecipientUid = @r ORDER BY SentAt DESC",
            connection);
        cmd.Parameters.AddWithValue("@r", recipientUid);
        using var reader = cmd.ExecuteReader();
        var results = new List<(string SenderUid, string Title, string Message, long SentAt, bool Delivered)>();
        while (reader.Read())
            results.Add((SenderUid: (string)reader["SenderUid"], Title: (string)reader["Title"], Message: (string)reader["Message"], SentAt: (long)reader["SentAt"], Delivered: (long)reader["Delivered"] == 1));
        return results;
    }

    public long GetLastSentAt(string senderUid)
    {
        using var connection = Open();
        using var cmd = new SqliteCommand("SELECT MAX(SentAt) FROM Pigeons WHERE SenderUid = @s", connection);
        cmd.Parameters.AddWithValue("@s", senderUid);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : 0L;
    }

    public void MarkDelivered(long id)
    {
        using var connection = Open();
        using var cmd = new SqliteCommand("UPDATE Pigeons SET Delivered = 1 WHERE Id = @id", connection);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
