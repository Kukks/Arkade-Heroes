// Procedural pixel-ICON renderer for gear/items — the item-world companion to
// hero-render.js. Pure + deterministic: the icon is a function of the item id
// (a kebab string like "arkforged-edge") only. Three slot families
// (Weapon = blade, Armor = shield, Trinket = gem), tier-tinted through a
// bronze -> steel -> gold material ramp, with a hash-driven per-item accent so
// the three items in a slot don't look identical. Transparent background: the
// icon drops onto any tile/frame. Entry points: renderItemDataUrl, itemMeta.
const S = 40;                                  // logical grid (square); the <img> upscales it crisply
const SLOTNAME = ['Weapon', 'Armor', 'Trinket'];
const TIERNAME = ['Common', 'Fine', 'Prime'];  // item tiers: the 500 / 2,500 / 10,000-sat catalog bands
const OUTLINE = [18, 20, 28];

// The fixed 9-item catalog (ItemCatalog.cs) mapped to slot + tier. Visual-domain
// data the icon module owns; unknown ids fall back to a hash-derived slot/tier.
const CATALOG = {
  'rusty-blade':   { slot: 0, tier: 0 }, 'steel-saber':   { slot: 0, tier: 1 }, 'arkforged-edge': { slot: 0, tier: 2 },
  'padded-vest':   { slot: 1, tier: 0 }, 'chain-hauberk': { slot: 1, tier: 1 }, 'covenant-plate': { slot: 1, tier: 2 },
  'lucky-feather': { slot: 2, tier: 0 }, 'swift-anklet':  { slot: 2, tier: 1 }, 'vtxo-charm':     { slot: 2, tier: 2 },
};

// tier material ramp: [dark, mid, light]
const MAT = [
  [[92, 64, 44], [150, 108, 70], [206, 168, 120]],   // 0 bronze
  [[92, 104, 122], [158, 172, 190], [226, 236, 248]], // 1 steel
  [[150, 104, 28], [226, 180, 66], [255, 238, 166]],  // 2 gold
];

/* ---------------- deterministic hash / rng / color ---------------- */
function strHash(s) { let h = 2166136261 >>> 0; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; } return h >>> 0; }
function rng(seed) { let x = (seed || 1) >>> 0; return () => { x ^= x << 13; x >>>= 0; x ^= x >>> 17; x ^= x << 5; x >>>= 0; return x / 4294967296; }; }
function hsl(h, s, l) {
  h = ((h % 360) + 360) % 360; s /= 100; l /= 100;
  const c = (1 - Math.abs(2 * l - 1)) * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = l - c / 2;
  let r, g, b;
  if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; } else if (h < 180) { r = 0; g = c; b = x; }
  else if (h < 240) { r = 0; g = x; b = c; } else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
  return [Math.round((r + m) * 255), Math.round((g + m) * 255), Math.round((b + m) * 255)];
}

function metaOf(id) {
  const c = CATALOG[id];
  if (c) return c;
  const h = strHash(id || '');
  return { slot: h % 3, tier: (h >>> 4) % 3 };
}

/* ---------------- families ---------------- */
// Each paints via px(x,y,[r,g,b]) into a transparent grid; mat = tier ramp, gem = accent ramp.
function drawWeapon(px, mat, gem, rnd, tier) {
  const cx = 20;
  const len = 5 + Math.round(rnd() * 3);        // tip y; longer blade = starts higher
  const guardY = 25, guardW = 6 + Math.round(rnd() * 2);
  // blade (tip up), 4 wide with an edge highlight (left) and shaded back (right)
  for (let y = len; y < guardY; y++) {
    const t = (y - len) / (guardY - len);        // 0 tip .. 1 guard
    const w = t < 0.18 ? 1 : 2;                   // taper near the tip
    px(cx - w, y, mat[2]); px(cx - w + 1, y, mat[2]);   // bright edge
    for (let k = -w + 2; k <= w; k++) px(cx + k, y, mat[1]);
    px(cx + w, y, mat[0]);                        // shaded back edge
    if (y > len + 2 && y < guardY - 2) px(cx, y, mat[0]); // central fuller groove
  }
  // crossguard
  for (let x = cx - guardW; x <= cx + guardW; x++) { px(x, guardY, mat[1]); px(x, guardY + 1, mat[0]); }
  px(cx - guardW, guardY - 1, mat[0]); px(cx + guardW, guardY - 1, mat[0]); // quillon tips lift
  // grip (wrapped) + pommel
  for (let y = guardY + 2; y <= 32; y++) { const w = (y % 2) ? mat[0] : mat[1]; px(cx - 1, y, w); px(cx, y, w); }
  for (let y = 33; y <= 35; y++) for (let x = cx - 2; x <= cx + 1; x++) px(x, y, mat[1]);
  if (tier >= 1) { px(cx - 1, 34, gem[2]); px(cx, 34, gem[1]); }   // pommel gem on Fine+
  if (tier >= 2) { px(cx - 3, len + 2, mat[2]); px(cx + 4, len + 3, mat[2]); } // gilt sparkle on Prime
}

function drawArmor(px, mat, gem, rnd, tier) {
  const cx = 20, top = 8, shW = 8 + Math.round(rnd() * 1), midY = 23, tipY = 34;
  // heater-shield silhouette, filled with a top-left light -> bottom-right shade gradient
  for (let y = top; y <= tipY; y++) {
    let half;
    if (y <= midY) half = shW;                                   // straight sides
    else half = Math.max(0, Math.round(shW * (1 - (y - midY) / (tipY - midY)))); // taper to a point
    for (let x = cx - half; x <= cx + half; x++) {
      const s = (x - cx) + (y - top) * 0.5;                      // diagonal light
      px(x, y, s < -shW * 0.4 ? mat[2] : s > shW * 0.5 ? mat[0] : mat[1]);
    }
  }
  // raised central rib + a boss with an emblem gem
  for (let y = top + 1; y <= midY + 3; y++) px(cx, y, mat[2]);
  for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++)
    if (dx * dx + dy * dy <= 5) px(cx + dx, 17 + dy, (dx + dy < 0) ? mat[2] : mat[0]);
  px(cx, 17, gem[tier >= 2 ? 2 : 1]);                            // boss centre
  if (tier >= 1) { px(cx, 16, gem[2]); px(cx, 18, gem[0]); }     // emblem gem grows with tier
}

function drawTrinket(px, mat, gem, rnd, tier) {
  const cx = 20, gy = 24, r = 6 + (tier >= 2 ? 1 : 0);
  // bail (loop) at the top, in the tier metal, with a short connector down to the gem
  for (let x = cx - 2; x <= cx + 2; x++) { px(x, 9, mat[1]); px(x, 12, mat[1]); }
  px(cx - 2, 10, mat[1]); px(cx - 2, 11, mat[1]); px(cx + 2, 10, mat[1]); px(cx + 2, 11, mat[1]);
  px(cx - 1, 10, [0, 0, 0]); px(cx, 10, [0, 0, 0]); px(cx + 1, 10, [0, 0, 0]); // loop hole
  for (let y = 13; y < gy - r + 1; y++) px(cx, y, mat[0]);                      // connector to the gem
  // faceted round gem: top-left facet light, bottom-right dark, sparkle
  for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
    if (dx * dx + dy * dy > r * r) continue;
    const s = dx + dy;
    px(cx + dx, gy + dy, s < -2 ? gem[2] : s > 3 ? gem[0] : gem[1]);
  }
  px(cx - 2, gy - 3, [255, 255, 255]); px(cx - 3, gy - 2, gem[2]);       // sparkle
  // metal claws holding the gem (N/E/S/W)
  px(cx, gy - r, mat[2]); px(cx, gy + r, mat[0]); px(cx - r, gy, mat[1]); px(cx + r, gy, mat[1]);
  if (tier >= 2) { px(cx + 3, gy - 3, [255, 255, 255]); px(cx - 4, gy + 3, gem[2]); } // extra sparkle on Prime
}

/* ---------------- compose ---------------- */
function paint(id) {
  const { slot, tier } = metaOf(id);
  const seed = strHash(id || '');
  const rnd = rng(seed);
  const mat = MAT[tier];
  const gemHue = (seed >>> 8) % 360;                 // per-item accent colour
  const gem = [hsl(gemHue, 70, 34), hsl(gemHue, 74, 54), hsl(gemHue, 60, 78)];

  const buf = new Uint8ClampedArray(S * S * 4);
  const px = (x, y, c, a = 255) => { x |= 0; y |= 0; if (x < 0 || y < 0 || x >= S || y >= S) return; const o = (y * S + x) * 4; buf[o] = c[0]; buf[o + 1] = c[1]; buf[o + 2] = c[2]; buf[o + 3] = a; };

  (slot === 0 ? drawWeapon : slot === 1 ? drawArmor : drawTrinket)(px, mat, gem, rnd, tier);

  // auto 1px dark outline: any transparent pixel touching an opaque one becomes outline
  const opaque = (x, y) => x >= 0 && y >= 0 && x < S && y < S && buf[(y * S + x) * 4 + 3] > 0;
  const edges = [];
  for (let y = 0; y < S; y++) for (let x = 0; x < S; x++) {
    if (opaque(x, y)) continue;
    if (opaque(x - 1, y) || opaque(x + 1, y) || opaque(x, y - 1) || opaque(x, y + 1)) edges.push(y * S + x);
  }
  for (const i of edges) { const o = i * 4; buf[o] = OUTLINE[0]; buf[o + 1] = OUTLINE[1]; buf[o + 2] = OUTLINE[2]; buf[o + 3] = 255; }
  return buf;
}

/** Render an item id to a transparent PNG data URL (a 40×40 icon; the <img>
 *  upscales it crisply via image-rendering:pixelated). `size` is advisory. */
export function renderItemDataUrl(id, size) {
  const buf = paint(id);
  const cnv = document.createElement('canvas'); cnv.width = S; cnv.height = S;
  const ctx = cnv.getContext('2d'); const img = ctx.createImageData(S, S);
  img.data.set(buf); ctx.putImageData(img, 0, 0);
  return cnv.toDataURL('image/png');
}

/** Slot/tier chrome for an item id (for labels + tinted frames). */
export function itemMeta(id) {
  const { slot, tier } = metaOf(id);
  return { slot, slotName: SLOTNAME[slot], tier, tierName: TIERNAME[tier] };
}
