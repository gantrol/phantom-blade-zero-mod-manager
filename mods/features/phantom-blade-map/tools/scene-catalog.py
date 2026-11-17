"""Prepare decoded FastGeo placements and build the offline scene ink catalog."""
import argparse
import base64
import hashlib
import json
import math
import struct
from pathlib import Path

import numpy as np


def transform(value):
    q = value["Rotation"]
    x, y, z, w = (q[k] for k in "XYZW")
    matrix = np.array([
        [1 - 2 * (y*y + z*z), 2 * (x*y - z*w), 2 * (x*z + y*w), 0],
        [2 * (x*y + z*w), 1 - 2 * (x*x + z*z), 2 * (y*z - x*w), 0],
        [2 * (x*z - y*w), 2 * (y*z + x*w), 1 - 2 * (x*x + y*y), 0],
        [0, 0, 0, 1]], dtype=float)
    matrix[:3, :3] *= [value["Scale3D"][k] for k in "XYZ"]
    matrix[:3, 3] = [value["Translation"][k] for k in "XYZ"]
    return matrix


def pose_transform(pose):
    pitch, yaw, roll = map(math.radians, pose["rotation"])
    sp, cp, sy, cy, sr, cr = math.sin(pitch), math.cos(pitch), math.sin(yaw), math.cos(yaw), math.sin(roll), math.cos(roll)
    matrix = np.eye(4)
    matrix[:3, :3] = np.array([[cp*cy, sr*sp*cy-cr*sy, -cr*sp*cy-sr*sy],
                              [cp*sy, sr*sp*sy+cr*cy, -cr*sp*sy+sr*cy], [sp, -sr*cp, cr*cp]]) * pose["scale"]
    matrix[:3, 3] = pose["translation"]
    return matrix


def placed_components(scene):
    rows, matrices = {}, {}
    for line in (scene / "components.jsonl").open(encoding="utf-8-sig"):
        row = json.loads(line)
        if row.get("pose") and "trash" not in row["package"].lower():
            rows[row["outerPath"] + "." + row["name"]] = row
    def world_matrix(key, active=None):
        if key in matrices:
            return matrices[key]
        active = set() if active is None else active
        if key in active:
            raise ValueError("Spline attachment cycle")
        active.add(key)
        pose = rows[key]["pose"]
        if any(pose[k] for k in ("absoluteLocation", "absoluteRotation", "absoluteScale")):
            raise ValueError("Unsupported spline absolute attachment")
        matrix = pose_transform(pose)
        if pose["parent"]:
            matrix = world_matrix(pose["parent"], active) @ matrix
        matrices[key] = matrix
        return matrix
    for key, row in rows.items():
        if row["class"] not in ("SplineMeshComponent", "LandscapeNaniteComponent") or not row["pose"]["mesh"]:
            continue
        props = {p["name"]: json.loads(p["value"]) for p in row["properties"]}
        if props.get("bVisible") is False or props.get("bHiddenInGame") or props.get("bRenderInMainPass") is False:
            continue
        mesh = props.get("StaticMesh")
        if not mesh or any(s in mesh["ObjectPath"].lower() for s in ("trash", "/foliage/", "/vfx/")):
            continue
        component = dict(id=key, mesh=mesh, matrix=world_matrix(key).tolist(), instances=None)
        if row["class"] == "SplineMeshComponent":
            spline = {k: props[k] for k in ("SplineParams", "SplineUpDir", "ForwardAxis", "SplineBoundaryMin", "SplineBoundaryMax", "bSmoothInterpRollScale") if k in props}
            if "SplineParams" not in spline:
                continue
            component["spline"] = spline
        yield component


def deform_spline(vertices, bounds, settings):
    # UE FSplineMeshSceneProxyDesc::CalcSliceTransform: Hermite centerline,
    # transported cross-section, interpolated roll/scale/offset, then world pose.
    params = settings["SplineParams"]
    axis = settings.get("ForwardAxis", 0)
    axis = int(axis) if str(axis).isdigit() else "XYZ".index(str(axis).split("::")[-1])
    origin = bounds["Origin"]["XYZ"[axis]]; extent = bounds["BoxExtent"]["XYZ"[axis]]
    lower, upper = settings.get("SplineBoundaryMin", 0), settings.get("SplineBoundaryMax", 0)
    custom = abs(upper-lower) > 1e-6
    if not custom:
        lower, upper = origin-extent, origin+extent
    if upper == lower:
        raise ValueError("Degenerate spline mesh bounds")
    alpha = (vertices[:, axis]-lower)/(upper-lower)
    min_t, max_t = (max(-4, (origin-extent-lower)/(upper-lower)), min(4, (origin+extent-lower)/(upper-lower))) if custom else (0, 1)
    t = np.clip(alpha, min_t, max_t)[:, None]
    def vector(name, default, keys="XYZ"):
        value = params.get(name)
        return np.array([value[k] for k in keys] if value else default)
    start, end = vector("StartPos", [0, 0, 0]), vector("EndPos", [100, 0, 0])
    a, b = vector("StartTangent", [100, 0, 0]), vector("EndTangent", [100, 0, 0])
    position = (2*t**3-3*t*t+1)*start + (t**3-2*t*t+t)*a + (-2*t**3+3*t*t)*end + (t**3-t*t)*b
    tangent = (6*t*t-6*t)*start + (3*t*t-4*t+1)*a + (-6*t*t+6*t)*end + (3*t*t-2*t)*b
    position += tangent*(alpha[:, None]-t)
    def normalize(v):
        return v / np.maximum(np.linalg.norm(v, axis=1, keepdims=True), 1e-8)
    direction = normalize(tangent); up = settings.get("SplineUpDir", dict(X=0, Y=0, Z=1))
    right = normalize(np.cross(np.array([up[k] for k in "XYZ"]), direction)); vertical = normalize(np.cross(direction, right))
    blend = alpha[:, None]
    if settings.get("bSmoothInterpRollScale"):
        blend = np.clip(blend, 0, 1); blend = blend*blend*(3-2*blend)
    offset = vector("StartOffset", [0, 0], "XY")*(1-blend) + vector("EndOffset", [0, 0], "XY")*blend
    position += right*offset[:, :1] + vertical*offset[:, 1:]
    roll = params.get("StartRoll", 0)*(1-blend) + params.get("EndRoll", 0)*blend
    across = np.cos(roll)*right - np.sin(roll)*vertical; normal = np.cos(roll)*vertical + np.sin(roll)*right
    scale = vector("StartScale", [1, 1], "XY")*(1-blend) + vector("EndScale", [1, 1], "XY")*blend
    first, second = ((1, 2), (2, 0), (0, 1))[axis]
    return position + across*vertices[:, first:first+1]*scale[:, :1] + normal*vertices[:, second:second+1]*scale[:, 1:]


def navigation_bounds(scene):
    # Authored convex volumes distinguish AI coverage cuts from real walls.
    rows = {}
    for line in (scene / "components.jsonl").open(encoding="utf-8-sig"):
        if "NavMeshBoundsVolume" not in line:
            continue
        row = json.loads(line)
        if "trash" not in row["package"].lower():
            rows[(row["package"], row["index"])] = row
    result = []
    for row in rows.values():
        if row["class"] != "BodySetup":
            continue
        parent = rows[(row["package"], row["outer"])]; pose = parent["pose"]
        if pose["parent"] or any(pose[k] for k in ("absoluteLocation", "absoluteRotation", "absoluteScale")):
            raise ValueError("Unsupported navigation volume attachment")
        pitch, yaw, roll = map(math.radians, pose["rotation"])
        sp, cp, sy, cy, sr, cr = math.sin(pitch), math.cos(pitch), math.sin(yaw), math.cos(yaw), math.sin(roll), math.cos(roll)
        rotation = np.array([[cp*cy, sr*sp*cy-cr*sy, -cr*sp*cy-sr*sy],
                             [cp*sy, sr*sp*sy+cr*cy, -cr*sp*sy+sr*cy], [sp, -sr*cp, cr*cp]])
        props = {p["name"]: json.loads(p["value"]) for p in row["properties"]}
        for element in props.get("AggGeom", {}).get("ConvexElems", []):
            points = np.array([[v[k] for k in "XYZ"] + [1] for v in element["VertexData"]])
            points = (points @ transform(element["Transform"]).T)[:, :3]
            points = (points * pose["scale"]) @ rotation.T + pose["translation"]
            center = points.mean(0); planes = []
            for face in np.array(element["IndexData"]).reshape(-1, 3):
                a, b, c = points[face]; normal = np.cross(b-a, c-a); length = np.linalg.norm(normal)
                if length < 1e-6:
                    continue
                normal /= length
                if np.dot(normal, center-a) > 0:
                    normal = -normal
                plane = [*normal.tolist(), float(np.dot(normal, a))]
                if not any(np.allclose(plane, p, atol=.001, rtol=0) for p in planes):
                    planes.append(plane)
            if len(planes) < 4:
                raise ValueError("Incomplete navigation bounds")
            result.append(dict(name=parent["outerPath"], min=points.min(0).tolist(), max=points.max(0).tolist(), planes=planes))
    if not result:
        raise ValueError("No authored navigation bounds decoded")
    return result


def prepare(scene, output, atlas):
    (output / "nav-bounds.json").write_text(json.dumps(navigation_bounds(scene)), encoding="utf-8")
    paths, components, instances = set(), 0, 0
    limits = None
    if atlas:
        data = json.loads(atlas.read_text(encoding="utf-8-sig").split("module.exports=", 1)[1].strip().removesuffix(";"))
        nodes = np.frombuffer(base64.b64decode(data["nodes"]), dtype="<f4").reshape(-1, 4)[:, :3]
        limits = nodes.min(0) - 5000, nodes.max(0) + 5000
    (output / "bounds.json").write_text(json.dumps([v.tolist() for v in limits] if limits else None), encoding="utf-8")
    with (output / "components.jsonl").open("w", encoding="utf-8") as writer:
        for file in sorted(scene.glob("*.fastgeo.json")):
            if "trash" in str(file).lower():
                continue
            data = json.loads(file.read_text(encoding="utf-8"))
            for cluster in data.get("ComponentClusters", []):
                for kind in ("StaticMeshComponents", "InstancedStaticMeshComponents"):
                    for component in cluster.get(kind, []):
                        proxy = component.get("SceneProxyDesc", {})
                        mesh = (proxy.get("StaticMeshSceneProxyDesc") or {}).get("StaticMesh")
                        primitive = proxy.get("PrimitiveSceneProxyDesc", {})
                        if not component.get("bVisible") or not mesh or primitive.get("bIsHidden") or primitive.get("bRenderInMainPass") is False:
                            continue
                        path = mesh["ObjectPath"]
                        if "trash" in path.lower() or "/Foliage/" in path or "/VFX/" in path:
                            continue
                        if limits:
                            bounds = component["WorldBounds"]
                            origin = np.array([bounds["Origin"][k] for k in "XYZ"])
                            extent = np.array([bounds["BoxExtent"][k] for k in "XYZ"])
                            if np.any(origin - extent > limits[1]) or np.any(origin + extent < limits[0]):
                                continue
                        row = dict(id=file.stem + ":" + str(component["ComponentIndex"]), mesh=mesh,
                                   transform=component["WorldTransform"], instances=component.get("PerInstanceSMData"))
                        writer.write(json.dumps(row, separators=(",", ":")) + "\n")
                        paths.add(path)
                        components += 1
                        instances += len(row["instances"]) if row["instances"] is not None else 1
        for row in placed_components(scene):
            writer.write(json.dumps(row, separators=(",", ":")) + "\n")
            paths.add(row["mesh"]["ObjectPath"]); components += 1; instances += 1
    (output / "references.json").write_text(json.dumps([dict(path=p, package=None) for p in sorted(paths)]), encoding="utf-8")
    (output / "packages.json").write_text("[]", encoding="utf-8")
    print(json.dumps(dict(components=components, instances=instances, models=len(paths))))


def build(plan, models, output):
    (output / "nav-bounds.json").write_bytes((plan / "nav-bounds.json").read_bytes())
    catalog = json.loads((models / "index.json").read_text(encoding="utf-8"))["models"]
    by_path = {}
    for model in catalog:
        if model["status"] != "render-mesh-decoded":
            continue
        path = model["path"].removesuffix(".uasset").removesuffix(".umap")
        if path.startswith("ProjectPBZ/Content/"):
            path = "/Game/" + path[len("ProjectPBZ/Content/"):]
        elif path.startswith("Engine/Content/"):
            path = "/Engine/" + path[len("Engine/Content/"):]
        by_path[(path + "." + str(model["export"])).casefold()] = model
        model["directory"] = str(models.resolve())
    limits = json.loads((plan / "bounds.json").read_text(encoding="utf-8"))
    unresolved, placed, small, outside = set(), 0, 0, 0
    deformed = {}
    with (output / "architecture.jsonl").open("w", encoding="utf-8") as writer:
        for line in (plan / "components.jsonl").open(encoding="utf-8"):
            component = json.loads(line)
            path = component["mesh"]["ObjectPath"]
            model = by_path.get(path.casefold())
            if model is None:
                unresolved.add(path)
                continue
            parent = np.array(component["matrix"]) if "matrix" in component else transform(component["transform"])
            bounds = json.loads(model["bounds"])
            if "spline" in component:
                identity = model["id"] + json.dumps(component["spline"], sort_keys=True)
                derived_id = hashlib.sha256(identity.encode()).hexdigest()[:24]
                if derived_id not in deformed:
                    data = (models / (model["id"] + ".mesh")).read_bytes()
                    magic, count, indices = struct.unpack("<III", data[:12])
                    if magic != 0x474D4250 or len(data) != 12 + count*12 + indices*4:
                        raise ValueError("Invalid source spline mesh")
                    points = np.frombuffer(data, dtype="<f4", count=count*3, offset=12).reshape(-1, 3)
                    points = deform_spline(points, bounds, component["spline"]).astype("<f4")
                    (models / (derived_id + ".mesh")).write_bytes(data[:12] + points.tobytes() + data[12+count*12:])
                    center, extent = (points.max(0)+points.min(0))/2, (points.max(0)-points.min(0))/2
                    derived_bounds = dict(Origin=dict(zip("XYZ", map(float, center))), BoxExtent=dict(zip("XYZ", map(float, extent))))
                    deformed[derived_id] = dict(model, id=derived_id, bounds=json.dumps(derived_bounds), representation="spline-deformed-"+model["representation"])
                model = deformed[derived_id]; bounds = json.loads(model["bounds"])
            extent = np.array([bounds["BoxExtent"][k] for k in "XYZ"])
            origin = np.array([bounds["Origin"][k] for k in "XYZ"])
            for index, instance in enumerate(component["instances"] if component["instances"] is not None else [None]):
                matrix = parent @ transform(instance["TransformData"]) if instance else parent
                world_extent = np.abs(matrix[:3, :3]) @ extent
                center = matrix[:3, :3] @ origin + matrix[:3, 3]
                if limits and (np.any(center-world_extent > limits[1]) or np.any(center+world_extent < limits[0])):
                    outside += 1
                    continue
                # Subpixel pebbles and debris do not contribute useful floor ink.
                if max(world_extent) * 2 < 50:
                    small += 1
                    continue
                writer.write(json.dumps(dict(id=component["id"] + ":" + str(index), model=model["id"], structural="spline" in component,
                                             mesh=path, visible=True, matrix=matrix[:3].tolist()), separators=(",", ":")) + "\n")
                placed += 1
    (output / "models.json").write_text(json.dumps(catalog + list(deformed.values()), separators=(",", ":")), encoding="utf-8")
    report = dict(source="decoded-fastgeo-landscape-and-spline-meshes", placements=placed, belowSampleSize=small, outsideMapBounds=outside,
                  unresolved=sorted(unresolved), splineModels=len(deformed), sampleCm=50, ink="ground-continuation", navigationModified=False)
    (output / "scene-source.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(dict(placements=placed, belowSampleSize=small, unresolved=len(unresolved))))


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("mode", choices=("prepare", "build"))
    parser.add_argument("--in", dest="source", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--models", type=Path)
    parser.add_argument("--atlas", type=Path)
    args = parser.parse_args()
    for path in (args.source, args.out, args.models, args.atlas):
        if path and "trash" in str(path.resolve()).lower():
            raise ValueError("Excluded path")
    args.out.mkdir(parents=True, exist_ok=True)
    if args.mode == "prepare":
        prepare(args.source, args.out, args.atlas)
    else:
        if args.models is None:
            parser.error("build requires --models")
        build(args.source, args.models, args.out)


if __name__ == "__main__":
    main()
