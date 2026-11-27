using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer;

/// <summary>
/// Component and placement commands, each one undo group. The component data is replaced whole on every edit (a
/// <see cref="OpKind.SetComponents"/> op with before and after copies), so undo restores it exactly. Parts snap: after a
/// move, a port within <see cref="SnapMetres"/> of a matching port facing it pulls the part onto it. Pipe ends on ports
/// follow their ports.
/// </summary>
public static class ComponentCommands
{
    public const double SnapMetres = 0.15;
    private const int Units = HullSolid.PositionUnitsPerMetre;

    public static string LastError { get; private set; } = "";

    public static ComponentData Data(DesignSession session) => session.Doc.Components ?? new ComponentData();

    public static uint AddPart(DesignSession session, PartKind kind, DefId material, Vector3d position, uint rotation = SolidRotation.Identity)
    {
        var id = session.Doc.AllocateId();
        Change(session, (s, data) =>
        {
            var part = PartGeometry.Default(kind);
            part.Id = id;
            part.Material = s.EnsureMaterial(material);
            part.Rotation = rotation;
            (part.PosX, part.PosY, part.PosZ) = Quantise(position);
            Snap(data, ref part);
            data.Parts.Add(part);
        });
        return id;
    }

    /// <summary>Replaces a part (parameters, position, rotation), snaps it, and moves the pipe ends on its ports.</summary>
    public static bool SetPart(DesignSession session, ComponentPart after, bool snap = true)
    {
        if (Data(session).IndexOfPart(after.Id) < 0)
            return false;

        Change(session, (_, data) =>
        {
            if (snap)
                Snap(data, ref after);

            data.Parts[data.IndexOfPart(after.Id)] = after;
        });
        return true;
    }

    public static bool SetPartMaterial(DesignSession session, uint id, DefId material)
    {
        var index = Data(session).IndexOfPart(id);

        if (index < 0)
            return false;

        Change(session, (s, data) =>
        {
            var part = data.Parts[index];
            part.Material = s.EnsureMaterial(material);
            data.Parts[index] = part;
        });
        return true;
    }

    /// <summary>Removes a part; pipe ends that were on it become open.</summary>
    public static bool RemovePart(DesignSession session, uint id)
    {
        if (Data(session).IndexOfPart(id) < 0)
            return false;

        Change(session, (_, data) =>
        {
            data.Parts.RemoveAt(data.IndexOfPart(id));

            foreach (var pipe in data.Pipes)
                pipe.Ports.RemoveAll(p => p.End.Part == id);
        });
        return true;
    }

    /// <summary>A pipe through <paramref name="nodes"/> (ship or component metres); ends on ports snap onto them.</summary>
    public static uint AddPipe(DesignSession session, PipeEnd from, PipeEnd to, IReadOnlyList<Vector3d> nodes, ushort outerMm, ushort innerMm, DefId material)
    {
        if (nodes.Count < 2)
        {
            LastError = "A pipe needs at least two nodes";
            return 0;
        }

        if (innerMm == 0 || innerMm >= outerMm)
        {
            LastError = "The inner diameter must be below the outer";
            return 0;
        }

        var id = session.Doc.AllocateId();
        Change(session, (s, data) =>
        {
            var points = new List<(int, int, int)>(nodes.Count);

            foreach (var n in nodes)
                points.Add(Quantise(n));

            data.Pipes.Add(PipeRoute.Between(id, from, to, points, outerMm, innerMm, s.EnsureMaterial(material)));
        });
        return id;
    }

    public static bool SetPipe(DesignSession session, uint id, Action<PipeRoute> edit)
    {
        if (Data(session).IndexOfPipe(id) < 0)
            return false;

        Change(session, (_, data) => edit(data.Pipes[data.IndexOfPipe(id)]));
        return true;
    }

    public static bool SetPipeMaterial(DesignSession session, uint id, DefId material)
    {
        if (Data(session).IndexOfPipe(id) < 0)
            return false;

        Change(session, (s, data) => data.Pipes[data.IndexOfPipe(id)].Material = s.EnsureMaterial(material));
        return true;
    }

    /// <summary>
    /// A new node at <paramref name="at"/> joined to node <paramref name="from"/> by a run the size of one already there:
    /// from an end it extends the pipe, from a node mid-pipe it branches (a tee). Returns the new node's index, or -1.
    /// </summary>
    public static int AddPipeNode(DesignSession session, uint id, int from, Vector3d at)
    {
        var index = Data(session).IndexOfPipe(id);

        if (index < 0 || from < 0 || from >= Data(session).Pipes[index].Nodes.Count)
            return -1;

        var pipe = Data(session).Pipes[index];

        if (pipe.Nodes.Count >= PipeRoute.MaxNodes || pipe.Runs.Count >= PipeRoute.MaxRuns)
        {
            LastError = "The pipe has the most nodes it can hold";
            return -1;
        }

        var node = pipe.Nodes.Count;
        Change(session, (_, data) =>
        {
            var p = data.Pipes[data.IndexOfPipe(id)];
            var near = p.RunAt(from);
            var (outer, inner) = near >= 0 ? (p.Runs[near].OuterMm, p.Runs[near].InnerMm) : (p.OuterMm, p.InnerMm);
            p.Nodes.Add(Quantise(at));
            p.Runs.Add(new PipeRun((byte)from, (byte)node, outer, inner));
        });
        return node;
    }

    /// <summary>Splits run <paramref name="run"/> at its middle with a new node (selected next). Returns its index, or -1.</summary>
    public static int SplitPipeRun(DesignSession session, uint id, int run)
    {
        var index = Data(session).IndexOfPipe(id);

        if (index < 0 || run < 0 || run >= Data(session).Pipes[index].Runs.Count || Data(session).Pipes[index].Nodes.Count >= PipeRoute.MaxNodes)
            return -1;

        var node = Data(session).Pipes[index].Nodes.Count;
        Change(session, (_, data) =>
        {
            var p = data.Pipes[data.IndexOfPipe(id)];
            var r = p.Runs[run];
            p.Nodes.Add(Quantise(Scale(Add(p.Node(r.A), p.Node(r.B)), 0.5)));
            p.Runs[run] = r with { B = (byte)node };
            p.Runs.Add(r with { A = (byte)node });
        });
        return node;
    }

    /// <summary>Removes a node (a node between two runs is dissolved); a pipe left with fewer than two nodes goes too.</summary>
    public static bool RemovePipeNode(DesignSession session, uint id, int node)
    {
        var index = Data(session).IndexOfPipe(id);

        if (index < 0 || node < 0 || node >= Data(session).Pipes[index].Nodes.Count)
            return false;

        if (Data(session).Pipes[index].Nodes.Count <= 2)
            return RemovePipe(session, id);

        Change(session, (_, data) => data.Pipes[data.IndexOfPipe(id)].RemoveNode(node));
        return true;
    }

    /// <summary>One run's diameters (mm); new runs from it take the same.</summary>
    public static bool SetPipeRun(DesignSession session, uint id, int run, ushort outerMm, ushort innerMm)
    {
        if (innerMm == 0 || innerMm >= outerMm)
        {
            LastError = "The inner diameter must be below the outer";
            return false;
        }

        var index = Data(session).IndexOfPipe(id);

        if (index < 0 || run < 0 || run >= Data(session).Pipes[index].Runs.Count)
            return false;

        Change(session, (_, data) =>
        {
            var p = data.Pipes[data.IndexOfPipe(id)];
            p.Runs[run] = p.Runs[run] with { OuterMm = outerMm, InnerMm = innerMm };
            (p.OuterMm, p.InnerMm) = (outerMm, innerMm);
        });
        return true;
    }

    public static bool RemovePipe(DesignSession session, uint id)
    {
        if (Data(session).IndexOfPipe(id) < 0)
            return false;

        Change(session, (_, data) => data.Pipes.RemoveAt(data.IndexOfPipe(id)));
        return true;
    }

    /// <summary>
    /// Places a prefab (a component plan) in this plan: the prefab is carried inside the plan by its hash, and the
    /// placement only says where. Returns the placement id, or 0.
    /// </summary>
    public static uint PlacePrefab(DesignSession session, PlanDocument prefab, Vector3d position, uint rotation = SolidRotation.Identity)
    {
        if (prefab.Level != PlanLevel.Component)
        {
            LastError = "Only component plans can be placed";
            return 0;
        }

        // Stored canonical, so the hash it is filed under is the hash of what is saved and sent.
        var canonical = prefab.Clone();
        PlanCanonical.Canonicalise(canonical);
        var hash = PlanFormat.HashBody(canonical).Value;
        prefab = session.Doc.Prefabs.TryGetValue(hash, out var held) ? held : canonical;
        var id = session.Doc.AllocateId();
        Change(session, (s, data) =>
        {
            if (!s.Doc.Prefabs.ContainsKey(hash))
                s.Apply(PlanOp.AddPrefab(hash, prefab));

            var placement = new ComponentPlacement { Id = id, Prefab = hash, Rotation = rotation };
            (placement.PosX, placement.PosY, placement.PosZ) = Quantise(position);
            data.Placements.Add(placement);
        });
        return id;
    }

    public static bool SetPlacement(DesignSession session, ComponentPlacement after)
    {
        if (Data(session).IndexOfPlacement(after.Id) < 0)
            return false;

        Change(session, (_, data) => data.Placements[data.IndexOfPlacement(after.Id)] = after);
        return true;
    }

    public static bool RemovePlacement(DesignSession session, uint id)
    {
        if (Data(session).IndexOfPlacement(id) < 0)
            return false;

        Change(session, (_, data) => data.Placements.RemoveAt(data.IndexOfPlacement(id)));
        return true;
    }

    /// <summary>A prefab's id: the hash of its canonical BODY.</summary>
    public static ulong PrefabHash(PlanDocument prefab)
    {
        var copy = prefab.Clone();
        PlanCanonical.Canonicalise(copy);
        return PlanFormat.HashBody(copy).Value;
    }

    /// <summary>
    /// Pulls <paramref name="part"/> onto the nearest matching port of another part within <see cref="SnapMetres"/>: the
    /// same kind, facing it, and free (not already joined to a third part or carrying a pipe). Translation only; the
    /// rotation is the user's.
    /// </summary>
    public static bool Snap(ComponentData data, ref ComponentPart part)
    {
        var ports = PartGeometry.Ports(part);
        var links = ComponentModel.Links(data);
        var best = SnapMetres;
        var move = default(Vector3d);
        var found = false;

        for (var i = 0; i < ports.Length; i++)
        {
            var (p, d) = PartGeometry.PortAt(part, i);

            foreach (var other in data.Parts)
            {
                if (other.Id == part.Id)
                    continue;

                var otherPorts = PartGeometry.Ports(other);

                for (var j = 0; j < otherPorts.Length; j++)
                {
                    if (otherPorts[j].Kind != ports[i].Kind || Taken(data, links, other.Id, j, part.Id))
                        continue;

                    var (q, e) = PartGeometry.PortAt(other, j);
                    var gap = Sub(q, p).Length();

                    if (gap < best && Dot(d, e) < -0.99)
                    {
                        best = gap;
                        move = Sub(q, p);
                        found = true;
                    }
                }
            }
        }

        if (!found)
            return false;

        var at = Add(part.Position, move);
        (part.PosX, part.PosY, part.PosZ) = Quantise(at);
        return true;
    }

    /// <summary>A port joined to some part other than <paramref name="moving"/>, or with a pipe end on it.</summary>
    private static bool Taken(ComponentData data, List<PortLink> links, uint part, int port, uint moving)
    {
        if (ComponentModel.Partner(links, part, port, out var other, out _) && other != moving)
            return true;

        foreach (var pipe in data.Pipes)
        {
            if (pipe.NodeOn(part, port) >= 0)
                return true;
        }

        return false;
    }

    /// <summary>How close a free pipe end must come to a free flow port to attach to it.</summary>
    public const double AttachMetres = 0.03;

    /// <summary>
    /// Moves every pipe node that sits on a port onto that port's current position, and attaches a free end (a node on one
    /// run, on no port) that has come within <see cref="AttachMetres"/> of a free flow port.
    /// </summary>
    public static void FollowPorts(ComponentData data)
    {
        var links = ComponentModel.Links(data);

        foreach (var pipe in data.Pipes)
        {
            pipe.Ports.RemoveAll(p => p.Node >= pipe.Nodes.Count || !Port(data, p.End, out _));

            foreach (var (node, end) in pipe.Ports)
            {
                if (Port(data, end, out var at))
                    pipe.Nodes[node] = Quantise(at);
            }

            foreach (var node in pipe.OpenEnds().ToList())
            {
                var p = pipe.Node(node);

                foreach (var part in data.Parts)
                {
                    var ports = PartGeometry.Ports(part);

                    for (var i = 0; i < ports.Length; i++)
                    {
                        if (ports[i].Kind != PortKind.Flow || Taken(data, links, part.Id, i, 0))
                            continue;

                        var at = PartGeometry.PortAt(part, i).Position;

                        if (Sub(at, p).Length() < AttachMetres)
                        {
                            pipe.Ports.Add(((byte)node, new PipeEnd(part.Id, (byte)i)));
                            pipe.Nodes[node] = Quantise(at);
                            goto attached;
                        }
                    }
                }

                attached:;
            }
        }
    }

    public static bool Port(ComponentData data, PipeEnd end, out Vector3d position)
    {
        position = default;
        var index = data.IndexOfPart(end.Part);

        if (index < 0 || end.Port >= PartGeometry.Ports(data.Parts[index]).Length)
            return false;

        position = PartGeometry.PortAt(data.Parts[index], end.Port).Position;
        return true;
    }

    private static void Change(DesignSession session, Action<DesignSession, ComponentData> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Components;
            var after = before?.Clone() ?? new ComponentData();
            mutate(s, after);
            FollowPorts(after);
            s.Apply(PlanOp.SetComponents(before, after.IsEmpty ? null : after));
        });

    public static (int X, int Y, int Z) Quantise(Vector3d v) => ((int)Math.Round(v.X * Units), (int)Math.Round(v.Y * Units), (int)Math.Round(v.Z * Units));
}
