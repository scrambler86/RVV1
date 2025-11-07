// Assets/Scripts/Runtime/ChunkManager.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using FishNet.Connection;
using Game.Networking.Adapters;

/// <summary>
/// Minimal ChunkManager compatibile con PlayerNetworkDriverFishNet.
/// - mantiene mappa entity/connection -> chunk cell (int x,y)
/// - fornisce CollectWithinRadius per invio AOI semplificato
/// - espone TryGetCellOf(ownerConnection, out (int x,int y))
/// - thread-unsafe, single-thread server main loop usage
/// </summary>
public class ChunkManager : MonoBehaviour
{
    public int cellSize = 128;
    public int activeRadiusCells = 4;

    // simple key for cells
    public struct Cell : IEquatable<Cell>
    {
        public int x, y;
        public Cell(int x, int y) { this.x = x; this.y = y; }
        public bool Equals(Cell other) => x == other.x && y == other.y;
        public override int GetHashCode() => (x * 397) ^ y;
        public override string ToString() => $"{x},{y}";
    }

    // player registration
    // maps connection -> last known cell
    readonly Dictionary<NetworkConnection, Cell> _connCell = new Dictionary<NetworkConnection, Cell>();

    // reverse index: cell -> set of connections in that cell
    readonly Dictionary<Cell, HashSet<NetworkConnection>> _cellMembers = new Dictionary<Cell, HashSet<NetworkConnection>>();

    // optional per-player bookkeeping
    readonly HashSet<NetworkConnection> _registered = new HashSet<NetworkConnection>();

    // Helpers
    Cell WorldToCell(Vector3 w)
    {
        int cx = Mathf.FloorToInt(w.x / (float)cellSize);
        int cy = Mathf.FloorToInt(w.z / (float)cellSize);
        return new Cell(cx, cy);
    }

    void AddConnToCell(NetworkConnection conn, Cell c)
    {
        if (!_cellMembers.TryGetValue(c, out var set))
        {
            set = new HashSet<NetworkConnection>();
            _cellMembers[c] = set;
        }
        set.Add(conn);
        _connCell[conn] = c;
    }

    void RemoveConnFromCell(NetworkConnection conn, Cell c)
    {
        if (_cellMembers.TryGetValue(c, out var set))
        {
            set.Remove(conn);
            if (set.Count == 0) _cellMembers.Remove(c);
        }
        if (_connCell.ContainsKey(conn)) _connCell.Remove(conn);
    }

    // Public API used by PlayerNetworkDriverFishNet

    public void RegisterPlayer(IPlayerNetworkDriver drv)
    {
        if (drv == null) return;
        var ownerConn = ResolveConnection(drv);
        if (ownerConn == null) return;
        if (_registered.Contains(ownerConn)) return;
        _registered.Add(ownerConn);
        // initialize in cell (at origin) until UpdatePlayerChunk provides a position
        if (!_connCell.ContainsKey(ownerConn))
        {
            AddConnToCell(ownerConn, new Cell(0, 0));
        }
    }

    public void UnregisterPlayer(IPlayerNetworkDriver drv)
    {
        if (drv == null) return;
        var ownerConn = ResolveConnection(drv);
        if (ownerConn == null) return;
        _registered.Remove(ownerConn);
        if (_connCell.TryGetValue(ownerConn, out var c))
        {
            RemoveConnFromCell(ownerConn, c);
        }
    }

    public void UpdatePlayerChunk(IPlayerNetworkDriver drv, Vector3 worldPos)
    {
        if (drv == null) return;
        var ownerConn = ResolveConnection(drv);
        if (ownerConn == null) return;

        Cell newCell = WorldToCell(worldPos);

        if (_connCell.TryGetValue(ownerConn, out var oldCell))
        {
            if (oldCell.Equals(newCell)) return;
            RemoveConnFromCell(ownerConn, oldCell);
        }
        AddConnToCell(ownerConn, newCell);
    }

    /// <summary>
    /// CollectWithinRadius - simple AOI: gather connections in rings around owner.
    /// ring 1 = own cell and immediate neighbors within radius 1, ring 2 = wider etc.
    /// Populates the provided HashSet with connections (excluding the owner optionally).
    /// </summary>
    public void CollectWithinRadius(NetworkConnection owner, int ring, HashSet<NetworkConnection> outSet, bool includeOwner = true)
    {
        if (outSet == null) return;
        outSet.Clear();
        if (owner == null) return;

        if (!_connCell.TryGetValue(owner, out var center)) return;

        int r = Mathf.Max(0, ring);
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                var c = new Cell(center.x + dx, center.y + dy);
                if (_cellMembers.TryGetValue(c, out var set))
                {
                    foreach (var conn in set)
                    {
                        if (!includeOwner && conn == owner) continue;
                        outSet.Add(conn);
                    }
                }
            }
    }

    // Overload matching driver call in PlayerNetworkDriverFishNet: CollectWithinRadius(Owner, ring, _tmpNear)
    public void CollectWithinRadius(NetworkConnection owner, int ring, HashSet<NetworkConnection> outSet)
        => CollectWithinRadius(owner, ring, outSet, true);

    // TryGetCellOf used by driver to fetch owner cell for packing anchor
    public bool TryGetCellOf(NetworkConnection owner, out (int x, int y) cell)
    {
        if (owner != null && _connCell.TryGetValue(owner, out var c))
        {
            cell = (c.x, c.y);
            return true;
        }
        cell = (0, 0);
        return false;
    }

    // Helper to resolve NetworkConnection from IPlayerNetworkDriver
    // Attempt common patterns: driver.Owner is a FishNet.Object.NetworkBehaviour.Owner? PlayerNetworkDriverFishNet used Owner property; if not accessible, caller can pass NetworkConnection directly.
    NetworkConnection ResolveConnection(IPlayerNetworkDriver drv)
    {
        if (drv == null) return null;
        // try reflection-ish: if drv implements MonoBehaviour we can attempt to find Owner via NetworkBehaviour on same component
        if (drv is MonoBehaviour mb)
        {
            // check for a NetworkBehaviour on the same GameObject that has an Owner property
            var nb = mb.GetComponent<FishNet.Object.NetworkBehaviour>();
            if (nb != null)
            {
                try
                {
                    // NetworkBehaviour.Owner is of type NetworkConnection; use it
                    return nb.Owner;
                }
                catch { }
            }

            // fallback: try to find a component with a NetworkObject and return its Owner
            var no = mb.GetComponent<FishNet.Object.NetworkObject>();
            if (no != null)
            {
                try { return no.Owner; } catch { }
            }
        }

        return null;
    }

    // Convenience overloads used by driver that pass Owner (NetworkConnection) directly
    public void RegisterPlayer(NetworkConnection conn)
    {
        if (conn == null) return;
        if (_registered.Contains(conn)) return;
        _registered.Add(conn);
        if (!_connCell.ContainsKey(conn)) AddConnToCell(conn, new Cell(0, 0));
    }

    public void UnregisterPlayer(NetworkConnection conn)
    {
        if (conn == null) return;
        _registered.Remove(conn);
        if (_connCell.TryGetValue(conn, out var c)) RemoveConnFromCell(conn, c);
    }

    public void UpdatePlayerChunk(NetworkConnection conn, Vector3 pos)
    {
        if (conn == null) return;
        Cell newCell = WorldToCell(pos);
        if (_connCell.TryGetValue(conn, out var oldCell))
        {
            if (oldCell.Equals(newCell)) return;
            RemoveConnFromCell(conn, oldCell);
        }
        AddConnToCell(conn, newCell);
    }
}