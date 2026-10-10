#!/usr/bin/env python3
"""Generates docs/mesh.html: a self-contained interactive 3D viewer for the
shipped .timbermesh models (no dependencies, no network: raw WebGL + embedded
JSON). Opens the actual mesh data we ship, so model changes can be inspected
without launching the game.

Features: orbit/zoom/pan, per-material coloring, baked node-animation playback
(sway/pulse, matching the game's NodeAnimationUpdater semantics), wireframe
mode, material legend, vertex/triangle counts.

Usage: view_mesh.py [out.html] [model.timbermesh ...]
"""

import json
import struct
import sys
import zlib
from pathlib import Path

MOD_ROOT = Path(__file__).resolve().parents[1]

# Approximate surface colors; water entries are the real _Color values from the
# extracted vanilla materials.
PALETTE = {
    "PaintedMetal.IronTeeth": [0.38, 0.54, 0.63],
    "PaintedMetal.Folktails": [0.77, 0.66, 0.36],
    "BaseWood_DarkBrown.IronTeeth": [0.43, 0.31, 0.20],
    "BaseWood_LightBrown.Folktails": [0.66, 0.53, 0.36],
    "BaseWood_Grey.IronTeeth": [0.55, 0.55, 0.55],
    "BaseMetal.IronTeeth": [0.60, 0.60, 0.62],
    "BaseMetal.Folktails": [0.60, 0.60, 0.62],
    "BreedingWater": [0.26, 0.33, 0.43],
    "AdvancedBreedingWater": [0.00, 0.40, 0.04],
    "Water": [0.15, 0.26, 0.30],
}


def rd_at(buf, p):
    shift, out = 0, 0
    while True:
        b = buf[p]
        p += 1
        out |= (b & 0x7F) << shift
        if not b & 0x80:
            return out, p
        shift += 7


def sub_fields(buf):
    p = 0
    while p < len(buf):
        key, p = rd_at(buf, p)
        f, wire = key >> 3, key & 7
        if wire == 0:
            v, p = rd_at(buf, p)
            yield f, v
        elif wire == 5:
            yield f, buf[p:p + 4]
            p += 4
        elif wire == 2:
            n, p = rd_at(buf, p)
            yield f, buf[p:p + n]
            p += n
        else:
            raise AssertionError(f"wire {wire}")


def floats(data, dim):
    return [struct.unpack_from("<" + "f" * dim, data, i * 4 * dim)
            for i in range(len(data) // (4 * dim))]


def fixed_floats(buf):
    return {f: struct.unpack("<f", v)[0] for f, v in sub_fields(buf) if len(v) == 4}


def parse_node(buf):
    nf = {}
    for f, v in sub_fields(buf):
        nf.setdefault(f, []).append(v)
    node = {
        "name": nf[2][0].decode(),
        "parent": nf.get(1, [-1])[0],
        "position": [0.0, 0.0, 0.0],
        "rotation": [0.0, 0.0, 0.0, 1.0],
        "scale": [1.0, 1.0, 1.0],
    }
    if 3 in nf:
        v = fixed_floats(nf[3][0])
        node["position"] = [v.get(1, 0.0), v.get(2, 0.0), v.get(3, 0.0)]
    if 4 in nf:
        q = fixed_floats(nf[4][0])
        node["rotation"] = [q.get(1, 0.0), q.get(2, 0.0), q.get(3, 0.0), q.get(4, 1.0)]
    if 5 in nf:
        s = fixed_floats(nf[5][0])
        node["scale"] = [s.get(1, 1.0), s.get(2, 1.0), s.get(3, 1.0)]

    props = {}
    for data in nf.get(7, []):
        fd = dict(sub_fields(data))
        props[fd[1].decode()] = fd[4]
    node["positions"] = floats(props["position"], 3)
    node["normals"] = floats(props["normal"], 3)
    node["uvs"] = floats(props["uv0"], 2) if "uv0" in props else None

    submeshes = []
    for data in nf.get(8, []):
        mf = dict(sub_fields(data))
        q, idxs = 0, []
        while q < len(mf[1]):
            val, q = rd_at(mf[1], q)
            idxs.append(val)
        submeshes.append({"material": mf[2].decode(), "indices": idxs})
    node["submeshes"] = submeshes

    for data in nf.get(10, []):          # NodeAnimations (take the first)
        af = {}
        for f, v in sub_fields(data):
            af.setdefault(f, []).append(v)
        frames = []
        for fr in af.get(3, []):
            ff = dict(sub_fields(fr))
            pos = fixed_floats(ff[1]) if 1 in ff else {}
            rot = fixed_floats(ff[2]) if 2 in ff else {}
            scl = fixed_floats(ff[3]) if 3 in ff else {}
            frames.append({
                "pos": [pos.get(1, 0.0), pos.get(2, 0.0), pos.get(3, 0.0)],
                "rot": [rot.get(1, 0.0), rot.get(2, 0.0), rot.get(3, 0.0), rot.get(4, 1.0)],
                "scale": [scl.get(1, 1.0), scl.get(2, 1.0), scl.get(3, 1.0)],
            })
        node["anim"] = {
            "name": af[1][0].decode(),
            "framerate": struct.unpack("<f", af[2][0])[0],
            "frames": frames,
        }
    return node


def parse_model(path: Path):
    raw = zlib.decompress(path.read_bytes())
    nodes = [parse_node(v) for f, v in sub_fields(raw) if f == 3]
    verts = sum(len(n["positions"]) for n in nodes)
    tris = sum(len(s["indices"]) // 3 for n in nodes for s in n["submeshes"])
    return {
        "name": path.stem,
        "nodes": nodes,
        "stats": {"verts": verts, "tris": tris},
    }


TEMPLATE = """<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>Teleporter Station: mesh viewer</title>
<style>
  body { margin: 0; overflow: hidden; background: #1b1e22; font-family: system-ui, sans-serif; }
  #ui { position: fixed; top: 0; left: 0; right: 0; padding: 8px 12px; display: flex;
        gap: 14px; align-items: center; background: rgba(20,22,25,.85); color: #cfd3d8;
        font-size: 13px; z-index: 2; flex-wrap: wrap; }
  #ui b { color: #fff; }
  #legend span { display: inline-flex; align-items: center; gap: 5px; margin-right: 10px; }
  #legend i { width: 12px; height: 12px; border-radius: 3px; display: inline-block; }
  canvas { display: block; }
  #help { position: fixed; bottom: 8px; left: 12px; color: #7d838a; font-size: 12px; z-index: 2; }
  label { display: inline-flex; gap: 4px; align-items: center; cursor: pointer; }
  select, input[type=range] { accent-color: #b69d69; }
</style>
</head>
<body>
<div id="ui">
  <b>mesh viewer</b>
  <select id="model"></select>
  <span id="stats"></span>
  <span id="legend"></span>
  <label><input type="checkbox" id="play" checked> play animation</label>
  <label>speed <input type="range" id="speed" min="0" max="3" step="0.1" value="1" style="width:80px"></label>
  <label><input type="checkbox" id="wire"> wireframe</label>
  <label><input type="checkbox" id="spin"> auto-rotate</label>
  <label><input type="checkbox" id="districtTint" checked> district tint</label>
  <input type="color" id="districtColor" value="#4fa8e8" style="width:30px;height:20px;padding:0;border:0;background:none;vertical-align:middle">
  <label>ring <select id="ringPattern">
    <option value="hex" selected>hex tread</option>
    <option value="panels">panels</option>
    <option value="smooth">smooth</option>
    <option value="ribs">ribs</option>
    <option value="plates">plates</option>
    <option value="fine">fine ribs</option>
      </select></label>
</div>
<div id="help">drag: orbit &middot; shift+drag / right-drag: pan &middot; wheel: zoom</div>
<script>
const DATA = %%DATA%%;

// ---- tiny mat/quat kit ----
function m4mul(a, b) {
  const o = new Float32Array(16);
  for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++)
    o[c*4+r] = a[r]*b[c*4] + a[4+r]*b[c*4+1] + a[8+r]*b[c*4+2] + a[12+r]*b[c*4+3];
  return o;
}
function m4perspective(fovy, aspect, near, far) {
  const f = 1 / Math.tan(fovy / 2), o = new Float32Array(16);
  o[0] = f / aspect; o[5] = f; o[10] = (far + near) / (near - far);
  o[11] = -1; o[14] = 2 * far * near / (near - far);
  return o;
}
function m4lookAt(eye, at, up) {
  let zx = eye[0]-at[0], zy = eye[1]-at[1], zz = eye[2]-at[2];
  let l = Math.hypot(zx, zy, zz); zx/=l; zy/=l; zz/=l;
  let xx = up[1]*zz - up[2]*zy, xy = up[2]*zx - up[0]*zz, xz = up[0]*zy - up[1]*zx;
  l = Math.hypot(xx, xy, xz); xx/=l; xy/=l; xz/=l;
  const yx = zy*xz - zz*xy, yy = zz*xx - zx*xz, yz = zx*xy - zy*xx;
  return new Float32Array([xx,yx,zx,0, xy,yy,zy,0, xz,yz,zz,0,
    -(xx*eye[0]+xy*eye[1]+xz*eye[2]), -(yx*eye[0]+yy*eye[1]+yz*eye[2]), -(zx*eye[0]+zy*eye[1]+zz*eye[2]), 1]);
}
function quatToMat3(q) {
  const [x,y,z,w] = q, x2=x+x, y2=y+y, z2=z+z;
  const xx=x*x2, xy=x*y2, xz=x*z2, yy=y*y2, yz=y*z2, zz=z*z2, wx=w*x2, wy=w*y2, wz=w*z2;
  return [1-(yy+zz), xy+wz, xz-wy, xy-wz, 1-(xx+zz), yz+wx, xz+wy, yz-wx, 1-(xx+yy)];
}
function slerp(a, b, t) {
  let [ax,ay,az,aw] = a, [bx,by,bz,bw] = b;
  let d = ax*bx+ay*by+az*bz+aw*bw;
  if (d < 0) { bx=-bx; by=-by; bz=-bz; bw=-bw; d=-d; }
  if (d > 0.9995) {
    return [ax+(bx-ax)*t, ay+(by-ay)*t, az+(bz-az)*t, aw+(bw-aw)*t];
  }
  const th = Math.acos(Math.min(1, d)), s = Math.sin(th);
  const wa = Math.sin((1-t)*th)/s, wb = Math.sin(t*th)/s;
  return [ax*wa+bx*wb, ay*wa+by*wb, az*wa+bz*wb, aw*wa+bw*wb];
}
function trsMatrix(pos, rot, scale) {
  const m3 = quatToMat3(rot);
  const m = new Float32Array(16);
  m[15] = 1;
  for (let c = 0; c < 3; c++)
    for (let r = 0; r < 3; r++)
      m[c*4+r] = m3[c*3+r] * scale[c];
  m[12] = pos[0]; m[13] = pos[1]; m[14] = pos[2];
  return m;
}
function normalMat(m) {  // inverse-transpose of upper-left 3x3
  const a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
  const A = e*i-f*h, B = f*g-d*i, C = d*h-e*g;
  let det = a*A + b*B + c*C; det = det === 0 ? 1 : det;
  const id = 1/det;
  // inverse (adjugate/det), then transpose == adjugate layout swap
  return new Float32Array([
    A*id, B*id, C*id,
    (c*h-b*i)*id, (a*i-c*g)*id, (b*g-a*h)*id,
    (b*f-c*e)*id, (c*d-a*f)*id, (a*e-b*d)*id,
  ]);
}

// ---- GL setup ----
const canvas = document.createElement("canvas");
document.body.appendChild(canvas);
const gl = canvas.getContext("webgl", { antialias: true });

const VS = `attribute vec3 aPos; attribute vec3 aNormal; attribute vec2 aUV;
uniform mat4 uMVP; uniform mat4 uModel; uniform mat3 uNormalMat;
varying vec3 vN; varying vec3 vWorld; varying vec2 vUV;
void main() {
  vec4 w = uModel * vec4(aPos, 1.0);
  vWorld = w.xyz;
  vN = normalize(uNormalMat * aNormal);
  vUV = aUV;
  gl_Position = uMVP * uModel * vec4(aPos, 1.0);
}`;
const FS = `precision mediump float;
varying vec3 vN; varying vec3 vWorld; varying vec2 vUV;
uniform vec3 uColor;
uniform vec3 uTint;
uniform sampler2D uTex;
uniform float uUseTex;
void main() {
  vec3 n = normalize(vN);
  vec3 l1 = normalize(vec3(0.45, 0.8, 0.5));
  vec3 l2 = normalize(vec3(-0.5, 0.25, -0.4));
  float d = max(dot(n, l1), 0.0) * 0.75 + max(dot(n, l2), 0.0) * 0.25;
  vec3 base = uUseTex > 0.5 ? texture2D(uTex, vUV).rgb * uTint : uColor;
  gl_FragColor = vec4(base * (0.35 + 0.75 * d), 1.0);
}`;
function shader(type, src) {
  const s = gl.createShader(type);
  gl.shaderSource(s, src); gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw gl.getShaderInfoLog(s);
  return s;
}
const prog = gl.createProgram();
gl.attachShader(prog, shader(gl.VERTEX_SHADER, VS));
gl.attachShader(prog, shader(gl.FRAGMENT_SHADER, FS));
gl.linkProgram(prog);
if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw gl.getProgramInfoLog(prog);
gl.useProgram(prog);
const loc = {
  aPos: gl.getAttribLocation(prog, "aPos"), aNormal: gl.getAttribLocation(prog, "aNormal"),
  aUV: gl.getAttribLocation(prog, "aUV"),
  uMVP: gl.getUniformLocation(prog, "uMVP"), uModel: gl.getUniformLocation(prog, "uModel"),
  uNormalMat: gl.getUniformLocation(prog, "uNormalMat"), uColor: gl.getUniformLocation(prog, "uColor"),
  uTint: gl.getUniformLocation(prog, "uTint"), uUseTex: gl.getUniformLocation(prog, "uUseTex"),
  uTex: gl.getUniformLocation(prog, "uTex"),
};
gl.enable(gl.DEPTH_TEST);

// ---- mesh upload ----
function edgesOf(indices) {
  const set = new Set(), out = [];
  for (let i = 0; i < indices.length; i += 3) {
    const t = [indices[i], indices[i+1], indices[i+2]];
    for (let k = 0; k < 3; k++) {
      const a = t[k], b = t[(k+1)%3], key = Math.min(a,b) + "_" + Math.max(a,b);
      if (!set.has(key)) { set.add(key); out.push(a, b); }
    }
  }
  return out;
}
function upload(model) {
  model.built = model.nodes.map(node => {
    const pos = new Float32Array(node.positions.flat());
    const nor = new Float32Array(node.normals.flat());
    const pb = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, pb); gl.bufferData(gl.ARRAY_BUFFER, pos, gl.STATIC_DRAW);
    const nb = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, nb); gl.bufferData(gl.ARRAY_BUFFER, nor, gl.STATIC_DRAW);
    let ub = null;
    if (node.uvs) {
      ub = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, ub);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(node.uvs.flat()), gl.STATIC_DRAW);
    }
    const subs = node.submeshes.map(sm => {
      const ib = gl.createBuffer();
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ib);
      gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array(sm.indices), gl.STATIC_DRAW);
      const edges = edgesOf(sm.indices);
      const eb = gl.createBuffer();
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, eb);
      gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array(edges), gl.STATIC_DRAW);
      return { material: sm.material, triCount: sm.indices.length, edgeCount: edges.length, ib, eb };
    });
    return { node, pb, nb, ub, subs };
  });
  // Dynamic UV buffer covering the root node: torus vertices get the selected
  // ring pattern, ramp vertices keep their baked UVs.
  delete model.ringOverride;
  const rootNode = model.nodes.find(n => n.name === model.name);
  if (rootNode && rootNode.uvs) {
    model.ringOverride = { buf: gl.createBuffer(), uvs: null };
    setRingPattern(model, document.getElementById("ringPattern").value);
  }
}

// ---- texture stage (diffuse atlases dumped by tools/dump_textures.py) ----
const texByFile = {};
function getTexture(file) {
  if (file in texByFile) return texByFile[file];
  texByFile[file] = null;
  const img = new Image();
  img.onload = () => {
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    texByFile[file] = t;
  };
  img.src = file;
  return null;
}

// ---- ring pattern override ----
// The torus is parametric, so alternative texture mappings are computed from
// vertex positions instead of being baked into the model. This lets ring
// patterns be compared live; whatever wins gets baked into the generator.
const TORUS = { cx: 0.5, cy: 0.515, cz: 0.5, R: 0.44, r: 0.075 };
const PANEL_U = { IronTeeth: 0.9697, Folktails: 0.5830 };  // bright panel texel (mirrors the generator)

function isTorusVertex(px, py, pz) {
  const hy = Math.hypot(px - TORUS.cx, py - TORUS.cy);
  return Math.abs(hy - TORUS.R) <= TORUS.r + 1e-4
      && Math.abs(pz - TORUS.cz) <= TORUS.r + 1e-4;
}

function ringPatternUV(pattern, px, py, pz, faction) {
  const phi = Math.atan2(py - TORUS.cy, px - TORUS.cx) / (2 * Math.PI) + 0.5;
  const hy = Math.hypot(px - TORUS.cx, py - TORUS.cy);
  const vt = Math.atan2(pz - TORUS.cz, hy - TORUS.R) / (2 * Math.PI) + 0.5;
  const fu = 0.1 + ((PANEL_U[faction] - 0.1) % 0.125);
  if (pattern === "smooth") return [fu, 0.5];
  if (pattern === "ribs") return [fu - 0.0625 + vt * 0.125, phi];
  if (pattern === "plates") return [0.02 + phi * 0.46, 0.52 + vt * 0.46];
  if (pattern === "fine") return [0.02 + phi * 0.46, 0.02 + vt * 0.46];
  if (pattern === "hex") {
    // Hex zone is ~4 columns wide; a 4-segment mirror wrap turns it into ~15
    // columns around the torus (grout lines land on every crease, so no
    // visible seam) with 2 rows on the tube; cells end up near-square.
    const m = Math.abs((phi * 4) % 2 - 1);
    return [0.762 + m * 0.235, 0.248 + vt * 0.181];
  }
  return [phi, 0.5];                       // "panels" = the baked mapping
}

function setRingPattern(model, pattern) {
  if (!model.ringOverride) return;
  const node = model.nodes.find(n => n.name === model.name);
  if (!node) return;
  const faction = model.name.endsWith("IronTeeth") ? "IronTeeth" : "Folktails";
  const out = new Float32Array(node.uvs.flat());
  for (let i = 0; i < node.positions.length; i++) {
    const p = node.positions[i];
    if (!isTorusVertex(p[0], p[1], p[2])) continue;
    const uv = ringPatternUV(pattern, p[0], p[1], p[2], faction);
    out[i * 2] = uv[0]; out[i * 2 + 1] = uv[1];
  }
  model.ringOverride.uvs = out;
  gl.bindBuffer(gl.ARRAY_BUFFER, model.ringOverride.buf);
  gl.bufferData(gl.ARRAY_BUFFER, out, gl.DYNAMIC_DRAW);
}

// ---- scene state ----
let current = DATA.models[0];
const sel = document.getElementById("model");
for (const m of DATA.models) {
  const o = document.createElement("option");
  o.value = m.name; o.textContent = m.name;
  sel.appendChild(o);
}
sel.onchange = () => { current = DATA.models.find(m => m.name === sel.value); refreshUI(); };
document.getElementById("ringPattern").onchange = e => setRingPattern(current, e.target.value);

// camera
const target = [0.5, 0.5, 0.5];
let yaw = -2.55, pitch = 0.45, dist = 2.4;   // -Z side: the in-game front

// grid
const gridVerts = [];
for (let i = -1; i <= 2.001; i += 0.25) {
  gridVerts.push(i, 0, -1, i, 0, 2, -1, 0, i, 2, 0, i);
}
const gridBuf = gl.createBuffer();
gl.bindBuffer(gl.ARRAY_BUFFER, gridBuf);
gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(gridVerts), gl.STATIC_DRAW);
const gridCount = gridVerts.length / 3;

function refreshUI() {
  upload(current);
  document.getElementById("stats").textContent =
    current.stats.verts + " verts, " + current.stats.tris + " tris";
  const legend = document.getElementById("legend");
  legend.innerHTML = "";
  const mats = new Set();
  for (const n of current.nodes) for (const s of n.submeshes) mats.add(s.material);
  for (const m of [...mats].sort()) {
    const c = DATA.palette[m] || [0.6, 0.6, 0.6];
    const span = document.createElement("span");
    span.innerHTML = "<i style='background:rgb(" + c.map(x => Math.round(x*255)).join(",") + ")'></i>" + m;
    legend.appendChild(span);
  }
}
refreshUI();

// ---- input ----
let dragging = 0, lastX = 0, lastY = 0;
canvas.onmousedown = e => { dragging = e.shiftKey || e.button === 2 ? 2 : 1; lastX = e.clientX; lastY = e.clientY; };
window.onmouseup = () => dragging = 0;
window.oncontextmenu = e => { if (e.target === canvas) e.preventDefault(); };
window.onmousemove = e => {
  if (!dragging) return;
  const dx = e.clientX - lastX, dy = e.clientY - lastY;
  lastX = e.clientX; lastY = e.clientY;
  if (dragging === 1) {
    yaw -= dx * 0.008;
    pitch = Math.max(-1.5, Math.min(1.5, pitch + dy * 0.008));
  } else {
    const s = dist * 0.0016;
    target[0] -= (Math.cos(yaw) * dx - Math.sin(yaw) * dy) * s;
    target[2] -= (Math.sin(yaw) * dx + Math.cos(yaw) * dy) * s;
    target[1] += dy * s;
  }
};
canvas.onwheel = e => {
  e.preventDefault();
  dist *= Math.pow(1.0015, e.deltaY);
};

// ---- animation (mirrors NodeAnimationUpdater: looped, lerp between frames) ----
let animTime = 0, lastT = 0;
function nodeLocalMatrix(node) {
  let pos = node.position, rot = node.rotation, scale = node.scale;
  if (node.anim && document.getElementById("play").checked) {
    const n = node.anim.frames.length;
    const len = n / node.anim.framerate;
    const nt = (animTime % len) / len;
    const fi = Math.floor(nt * n) % n;
    const fn = (fi + 1) % n;
    const w = nt * n % 1;
    const a = node.anim.frames[fi], b = node.anim.frames[fn];
    pos = a.pos.map((v, i) => v + (b.pos[i] - v) * w);
    rot = slerp(a.rot, b.rot, w);
    scale = a.scale.map((v, i) => v + (b.scale[i] - v) * w);
  }
  return trsMatrix(pos, rot, scale);
}

function resize() {
  const dpr = window.devicePixelRatio || 1;
  canvas.width = innerWidth * dpr; canvas.height = innerHeight * dpr;
  canvas.style.width = innerWidth + "px"; canvas.style.height = innerHeight + "px";
  gl.viewport(0, 0, canvas.width, canvas.height);
}
window.onresize = resize;
resize();

// In-game TeleporterNode tints the horizon nodes with a per-district color
// (disc darkened to a void, arms at full brightness), dividing out the plaster
// texel so the sampled texture lands on the pure hue. Mirrored here.
const HORIZON_TEXEL = [0.757, 0.706, 0.663];
function districtTint() {
  if (!document.getElementById("districtTint").checked) return null;
  const hex = document.getElementById("districtColor").value;
  return [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255);
}

function frame(now) {
  const dt = Math.min(0.05, (now - lastT) / 1000 || 0.016);
  lastT = now;
  if (document.getElementById("spin").checked) yaw += dt * 0.4;

  gl.clearColor(0.106, 0.118, 0.133, 1);
  gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

  const eye = [
    target[0] + dist * Math.cos(pitch) * Math.sin(yaw),
    target[1] + dist * Math.sin(pitch),
    target[2] + dist * Math.cos(pitch) * Math.cos(yaw),
  ];
  const view = m4lookAt(eye, target, [0, 1, 0]);
  const proj = m4perspective(0.9, canvas.width / canvas.height, 0.01, 100);

  gl.bindBuffer(gl.ARRAY_BUFFER, gridBuf);
  gl.enableVertexAttribArray(loc.aPos);
  gl.vertexAttribPointer(loc.aPos, 3, gl.FLOAT, false, 0, 0);
  gl.disableVertexAttribArray(loc.aNormal);
  gl.vertexAttrib3f(loc.aNormal, 0, 1, 0);
  const gridModel = new Float32Array([1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1]);
  gl.uniformMatrix4fv(loc.uModel, false, gridModel);
  gl.uniformMatrix3fv(loc.uNormalMat, false, new Float32Array([1,0,0, 0,1,0, 0,0,1]));
  gl.uniformMatrix4fv(loc.uMVP, false, m4mul(proj, view));
  gl.uniform3f(loc.uColor, 0.24, 0.26, 0.24);
  gl.drawArrays(gl.LINES, 0, gridCount);
  gl.enableVertexAttribArray(loc.aNormal);

  const wire = document.getElementById("wire").checked;
  gl.enable(gl.CULL_FACE);
  gl.cullFace(gl.BACK);
  const world = new Array(current.nodes.length);
  if (document.getElementById("play").checked) {
    animTime += dt * parseFloat(document.getElementById("speed").value);
  }
  for (let i = 0; i < current.nodes.length; i++) {
    const node = current.nodes[i];
    const local = nodeLocalMatrix(node);
    world[i] = (node.parent >= 0 && node.parent < i) ? m4mul(world[node.parent], local) : local;
  }
  for (let i = 0; i < current.built.length; i++) {
    const b = current.built[i];
    const node = current.nodes[i];
    const model = world[i];
    const m3 = normalMat(model);
    gl.bindBuffer(gl.ARRAY_BUFFER, b.pb);
    gl.vertexAttribPointer(loc.aPos, 3, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, b.nb);
    gl.vertexAttribPointer(loc.aNormal, 3, gl.FLOAT, false, 0, 0);
    const useRingUV = current.ringOverride && b.node.name === current.name;
    const uvBuf = useRingUV ? current.ringOverride.buf : b.ub;
    if (uvBuf) {
      gl.bindBuffer(gl.ARRAY_BUFFER, uvBuf);
      gl.vertexAttribPointer(loc.aUV, 2, gl.FLOAT, false, 0, 0);
      gl.enableVertexAttribArray(loc.aUV);
    } else {
      gl.disableVertexAttribArray(loc.aUV);
    }
    gl.uniformMatrix4fv(loc.uModel, false, model);
    gl.uniformMatrix3fv(loc.uNormalMat, false, m3);
    const isHorizon = node && (node.name === "Horizon" || node.name === "Arms");
    const dark = isHorizon && node.name === "Horizon" ? 0.22 : 1.0;
    const pattern = b.node.name === current.name
        ? document.getElementById("ringPattern").value : "panels";
    for (const sm of b.subs) {
      let col = DATA.palette[sm.material] || [0.6, 0.6, 0.6];
      const picked = isHorizon ? districtTint() : null;
      let info = (!wire && DATA.textures) ? DATA.textures[sm.material] : null;
      if (pattern && sm.material.indexOf("BaseMetal") === 0) {
        const faction = current.name.endsWith("IronTeeth") ? "IronTeeth" : "Folktails";
        const want = (pattern === "plates" || pattern === "fine" || pattern === "hex")
            ? "BaseMetal." + faction : "PaintedMetal." + faction;
        info = (DATA.textures && DATA.textures[want]) || info;
      }
      const tex = info ? getTexture(info.diffuse) : null;
      if (tex) {
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.uniform1f(loc.uUseTex, 1);
        const c = info.color;
        gl.uniform3f(loc.uTint,
          (picked ? picked[0] / HORIZON_TEXEL[0] : c[0]) * dark,
          (picked ? picked[1] / HORIZON_TEXEL[1] : c[1]) * dark,
          (picked ? picked[2] / HORIZON_TEXEL[2] : c[2]) * dark);
      } else {
        gl.uniform1f(loc.uUseTex, 0);
        if (picked) col = [picked[0] * dark, picked[1] * dark, picked[2] * dark];
        gl.uniform3fv(loc.uColor, col);
      }
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, wire ? sm.eb : sm.ib);
      gl.drawElements(wire ? gl.LINES : gl.TRIANGLES, wire ? sm.edgeCount : sm.triCount, gl.UNSIGNED_SHORT, 0);
    }
  }
  requestAnimationFrame(frame);
}
requestAnimationFrame(frame);
</script>
</body>
</html>
"""


THUMBNAIL_TEMPLATE = """<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>Teleporter Station: workshop thumbnail</title>
<style>
  body { margin: 0; background: #16181c; color: #c7c8c8; font: 13px sans-serif;
         display: flex; flex-direction: column; align-items: center; gap: 8px;
         min-height: 100vh; justify-content: center; }
  canvas#out { max-width: 96vw; max-height: 88vh; border-radius: 6px; }
</style>
</head>
<body>
<canvas id="out" width="1920" height="1080"></canvas>
<div>workshop thumbnail preview: 1920x1080, saved as thumbnail.png</div>
<script>
const DATA = %%DATA%%;

// ---- mat/quat kit (identical semantics to the mesh viewer) ----
function m4mul(a, b) {
  const o = new Float32Array(16);
  for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++)
    o[c*4+r] = a[r]*b[c*4] + a[4+r]*b[c*4+1] + a[8+r]*b[c*4+2] + a[12+r]*b[c*4+3];
  return o;
}
function m4perspective(fovy, aspect, near, far) {
  const f = 1 / Math.tan(fovy / 2), o = new Float32Array(16);
  o[0] = f / aspect; o[5] = f; o[10] = (far + near) / (near - far);
  o[11] = -1; o[14] = 2 * far * near / (near - far);
  return o;
}
function m4lookAt(eye, at, up) {
  let zx = eye[0]-at[0], zy = eye[1]-at[1], zz = eye[2]-at[2];
  let l = Math.hypot(zx, zy, zz); zx/=l; zy/=l; zz/=l;
  let xx = up[1]*zz - up[2]*zy, xy = up[2]*zx - up[0]*zz, xz = up[0]*zy - up[1]*zx;
  l = Math.hypot(xx, xy, xz); xx/=l; xy/=l; xz/=l;
  const yx = zy*xz - zz*xy, yy = zz*xx - zx*xz, yz = zx*xy - zy*xx;
  return new Float32Array([xx,yx,zx,0, xy,yy,zy,0, xz,yz,zz,0,
    -(xx*eye[0]+xy*eye[1]+xz*eye[2]), -(yx*eye[0]+yy*eye[1]+yz*eye[2]), -(zx*eye[0]+zy*eye[1]+zz*eye[2]), 1]);
}
function quatToMat3(q) {
  const [x,y,z,w] = q, x2=x+x, y2=y+y, z2=z+z;
  const xx=x*x2, xy=x*y2, xz=x*z2, yy=y*y2, yz=y*z2, zz=z*z2, wx=w*x2, wy=w*y2, wz=w*z2;
  return [1-(yy+zz), xy+wz, xz-wy, xy-wz, 1-(xx+zz), yz+wx, xz+wy, yz-wx, 1-(xx+yy)];
}
function trsMatrix(pos, rot, scale) {
  const m3 = quatToMat3(rot);
  const m = new Float32Array(16);
  m[15] = 1;
  for (let c = 0; c < 3; c++)
    for (let r = 0; r < 3; r++)
      m[c*4+r] = m3[c*3+r] * scale[c];
  m[12] = pos[0]; m[13] = pos[1]; m[14] = pos[2];
  return m;
}
function normalMat(m) {
  const a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
  const A = e*i-f*h, B = f*g-d*i, C = d*h-e*g;
  let det = a*A + b*B + c*C; det = det === 0 ? 1 : det;
  const id = 1/det;
  return new Float32Array([
    A*id, B*id, C*id,
    (c*h-b*i)*id, (a*i-c*g)*id, (b*g-a*h)*id,
    (b*f-c*e)*id, (c*d-a*f)*id, (a*e-b*d)*id,
  ]);
}

// ---- GL (same two-light shading as the mesh viewer) ----
const W = 1920, H = 1080;
const glCanvas = document.createElement("canvas");
glCanvas.width = W; glCanvas.height = H;
const gl = glCanvas.getContext("webgl", { antialias: true, preserveDrawingBuffer: true });
const VS = `attribute vec3 aPos; attribute vec3 aNormal; attribute vec2 aUV;
uniform mat4 uMVP; uniform mat4 uModel; uniform mat3 uNormalMat;
varying vec3 vN; varying vec3 vWorld; varying vec2 vUV;
void main() {
  vec4 w = uModel * vec4(aPos, 1.0);
  vWorld = w.xyz;
  vN = normalize(uNormalMat * aNormal);
  vUV = aUV;
  gl_Position = uMVP * uModel * vec4(aPos, 1.0);
}`;
const FS = `precision mediump float;
varying vec3 vN; varying vec3 vWorld; varying vec2 vUV;
uniform vec3 uColor;
uniform vec3 uTint;
uniform sampler2D uTex;
uniform float uUseTex;
void main() {
  vec3 n = normalize(vN);
  vec3 l1 = normalize(vec3(0.45, 0.8, 0.5));
  vec3 l2 = normalize(vec3(-0.5, 0.25, -0.4));
  float d = max(dot(n, l1), 0.0) * 0.75 + max(dot(n, l2), 0.0) * 0.25;
  vec3 base = uUseTex > 0.5 ? texture2D(uTex, vUV).rgb * uTint : uColor;
  gl_FragColor = vec4(base * (0.35 + 0.75 * d), 1.0);
}`;
function shader(type, src) {
  const s = gl.createShader(type);
  gl.shaderSource(s, src); gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw gl.getShaderInfoLog(s);
  return s;
}
const prog = gl.createProgram();
gl.attachShader(prog, shader(gl.VERTEX_SHADER, VS));
gl.attachShader(prog, shader(gl.FRAGMENT_SHADER, FS));
gl.linkProgram(prog);
if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw gl.getProgramInfoLog(prog);
gl.useProgram(prog);
const loc = {
  aPos: gl.getAttribLocation(prog, "aPos"), aNormal: gl.getAttribLocation(prog, "aNormal"),
  aUV: gl.getAttribLocation(prog, "aUV"),
  uMVP: gl.getUniformLocation(prog, "uMVP"), uModel: gl.getUniformLocation(prog, "uModel"),
  uNormalMat: gl.getUniformLocation(prog, "uNormalMat"), uColor: gl.getUniformLocation(prog, "uColor"),
  uTint: gl.getUniformLocation(prog, "uTint"), uUseTex: gl.getUniformLocation(prog, "uUseTex"),
  uTex: gl.getUniformLocation(prog, "uTex"),
};
gl.enable(gl.DEPTH_TEST);
gl.enable(gl.CULL_FACE);
gl.cullFace(gl.BACK);

// District hues mirrored from TeleporterNode (disc = 22% void, arms = full).
const TINTS = {
  "Teleporter.IronTeeth": [0.31, 0.66, 0.91],
  "Teleporter.Folktails": [0.37, 0.77, 0.42],
};

function build(model) {
  return model.nodes.map(node => {
    const pb = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, pb);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(node.positions.flat()), gl.STATIC_DRAW);
    const nb = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, nb);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(node.normals.flat()), gl.STATIC_DRAW);
    let ub = null;
    if (node.uvs) {
      ub = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, ub);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(node.uvs.flat()), gl.STATIC_DRAW);
    }
    const subs = node.submeshes.map(sm => {
      const ib = gl.createBuffer();
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ib);
      gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array(sm.indices), gl.STATIC_DRAW);
      return { ib, count: sm.indices.length, material: sm.material };
    });
    return { node, pb, nb, ub, subs };
  });
}

// Diffuse atlases dumped by tools/dump_textures.py; absent files fall back to
// flat palette colors.
const texByFile = {};
function getTexture(file) {
  if (file in texByFile) return texByFile[file];
  texByFile[file] = null;
  const img = new Image();
  img.onload = () => {
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    texByFile[file] = t;
    render();                    // textures arrive after the first frame
  };
  img.src = file;
  return null;
}

function drawModel(built, name, vpX, vpW) {
  const tint = TINTS[name] || [0.6, 0.6, 0.6];
  const target = [0.5, 0.38, 0.5];
  const eye = [
    target[0] + 2.15 * Math.cos(0.45) * Math.sin(-2.55),
    target[1] + 2.15 * Math.sin(0.45),
    target[2] + 2.15 * Math.cos(0.45) * Math.cos(-2.55),
  ];
  const view = m4lookAt(eye, target, [0, 1, 0]);
  const proj = m4perspective(0.9, vpW / H, 0.01, 100);
  gl.uniformMatrix4fv(loc.uMVP, false, m4mul(proj, view));
  const world = new Array(built.length);
  for (let i = 0; i < built.length; i++) {
    const node = built[i].node;
    const local = trsMatrix(node.position, node.rotation, node.scale);
    world[i] = (node.parent >= 0 && node.parent < i) ? m4mul(world[node.parent], local) : local;
  }
  gl.viewport(vpX, 0, vpW, H);
  for (let i = 0; i < built.length; i++) {
    const b = built[i];
    gl.bindBuffer(gl.ARRAY_BUFFER, b.pb);
    gl.vertexAttribPointer(loc.aPos, 3, gl.FLOAT, false, 0, 0);
    gl.enableVertexAttribArray(loc.aPos);
    gl.bindBuffer(gl.ARRAY_BUFFER, b.nb);
    gl.vertexAttribPointer(loc.aNormal, 3, gl.FLOAT, false, 0, 0);
    gl.enableVertexAttribArray(loc.aNormal);
    if (b.ub) {
      gl.bindBuffer(gl.ARRAY_BUFFER, b.ub);
      gl.vertexAttribPointer(loc.aUV, 2, gl.FLOAT, false, 0, 0);
      gl.enableVertexAttribArray(loc.aUV);
    } else {
      gl.disableVertexAttribArray(loc.aUV);
    }
    gl.uniformMatrix4fv(loc.uModel, false, world[i]);
    gl.uniformMatrix3fv(loc.uNormalMat, false, normalMat(world[i]));
    const isHorizon = b.node.name === "Horizon" || b.node.name === "Arms";
    const dark = isHorizon && b.node.name === "Horizon" ? 0.22 : 1.0;
    for (const sm of b.subs) {
      let col = DATA.palette[sm.material] || [0.6, 0.6, 0.6];
      const info = DATA.textures && DATA.textures[sm.material];
      const tex = info ? getTexture(info.diffuse) : null;
      if (tex) {
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.uniform1f(loc.uUseTex, 1);
        const c = info.color;
        gl.uniform3f(loc.uTint,
          (isHorizon ? tint[0] / 0.757 : c[0]) * dark,
          (isHorizon ? tint[1] / 0.706 : c[1]) * dark,
          (isHorizon ? tint[2] / 0.663 : c[2]) * dark);
      } else {
        gl.uniform1f(loc.uUseTex, 0);
        if (isHorizon) col = [tint[0] * dark, tint[1] * dark, tint[2] * dark];
        gl.uniform3fv(loc.uColor, col);
      }
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, sm.ib);
      gl.drawElements(gl.TRIANGLES, sm.count, gl.UNSIGNED_SHORT, 0);
    }
  }
}

const built = DATA.models.map(m => build(m));
const out = document.getElementById("out");
const ctx = out.getContext("2d");

function render() {
  gl.viewport(0, 0, W, H);
  gl.clearColor(0.106, 0.118, 0.133, 1);
  gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
  drawModel(built[0], DATA.models[0].name, 0, W / 2);
  drawModel(built[1], DATA.models[1].name, W / 2, W / 2);

  // gradient backdrop + GL render + title
  const grad = ctx.createLinearGradient(0, 0, 0, H);
  grad.addColorStop(0, "rgb(46,52,60)");
  grad.addColorStop(1, "rgb(30,33,38)");
  ctx.fillStyle = grad;
  ctx.fillRect(0, 0, W, H);
  ctx.drawImage(glCanvas, 0, 0);
  ctx.fillStyle = "rgb(226,221,207)";
  ctx.font = "bold 84px 'Open Sans', 'Droid Sans', sans-serif";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText("TELEPORTER STATION", W / 2, H * 0.11);
}
render();
</script>
</body>
</html>
"""


def main() -> None:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else MOD_ROOT / "docs" / "mesh.html"
    model_paths = [Path(a) for a in sys.argv[2:]] or sorted(MOD_ROOT.glob("*.timbermesh"))
    models = [parse_model(p) for p in model_paths]
    data = {"palette": PALETTE, "models": models}
    # Real atlas textures for textured rendering (dumped by dump_textures.py;
    # the viewer falls back to flat palette colors when the dump is absent).
    texmap = MOD_ROOT / "docs" / "textures" / "textures.json"
    if texmap.exists():
        data["textures"] = json.loads(texmap.read_text())
    html = TEMPLATE.replace("%%DATA%%", json.dumps(data, separators=(",", ":")))
    out.write_text(html)
    thumb = MOD_ROOT / "docs" / "thumbnail.html"
    thumb.write_text(
        THUMBNAIL_TEMPLATE.replace("%%DATA%%", json.dumps(data, separators=(",", ":"))))
    total = sum(m["stats"]["verts"] for m in models)
    print(f"wrote {out} ({len(html) // 1024} KB, {len(models)} models, {total} verts)")
    print(f"wrote {thumb}")


if __name__ == "__main__":
    main()
