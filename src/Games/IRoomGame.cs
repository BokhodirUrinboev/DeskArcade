using System.Net;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>A card game for 2–4 players in a <see cref="RoomLink"/> room, set up in <see cref="RoomWindow"/>.</summary>
public interface IRoomGame
{
    string Id { get; }
    string Title { get; }
    RoomLink Room { get; }
    /// <summary>The oldest Desk Arcade that can play this game in a room, for the setup window.</summary>
    string MinVersion { get; }
    bool Playing { get; }
    /// <summary>A game against <paramref name="cpus"/> computer players, no network.</summary>
    void StartSolo(int cpus);
    void HostRoom(string? code = null);
    void JoinRoom(string code, IPEndPoint? address);
    /// <summary>Host: starts with everyone in the room, computer players filling up to <paramref name="players"/> seats.</summary>
    void StartRoom(int players);
    void LeaveRoom();
}
