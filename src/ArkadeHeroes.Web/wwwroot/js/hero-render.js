// Procedural pixel-creature renderer — ported verbatim from the approved mockup
// (hero-pixel-mock.html). Pure + deterministic: renderHero is a function of the
// 32-byte genome only. The Blazor entry points (renderHeroDataUrl, heroMeta) are
// appended at the end; the studio DOM wiring is intentionally NOT included.
const ELEMENTS=['Ember','Gale','Terra','Tide','Volt','Frost','Radiant','Umbral'];
const TIERNAME=['COMMON','UNCOMMON','RARE','EPIC','LEGENDARY'];
const TIERCOL=['#9aa3b2','#43c96f','#3b9dff','#b45cff','#f5c542'];
const TIER_WEIGHT=[1,3,8,20,50];   // rarity score weights, ascending per tier (Common..Legendary)
const CATNAME=['Aura','Marking','Eyes','Crest','Sigil','Stance','Affinity','Temperament'];
function parseHex(hex){ const b=new Uint8Array(32);
  for(let i=0;i<32;i++) b[i]=parseInt(hex.substr(i*2,2),16)||0; return b; }
function tierOf(v){ if(v>=255)return 4; if(v>=253)return 3; if(v>=241)return 2; if(v>=206)return 1; return 0; }
function hero(hex){
  const g=parseHex(hex);
  const trait=c=>g[16+c*2];                       // dominant byte
  const expressed=[]; for(let c=0;c<8;c++) if(trait(c)>0) expressed.push({cat:c,v:trait(c),tier:tierOf(trait(c))});
  const tier=expressed.length?Math.max(...expressed.map(t=>t.tier)):0;
  return { g, hex, element:g[5]%8, expressed, tier,
    tval:c=>trait(c), ttier:c=>trait(c)>0?tierOf(trait(c)):-1,
    gen0:expressed.length===0 };
}

/* ---------------- proportion profiles (ONLY axis that changes de-chibi) ----------------
   Everything else (pipeline, ramps, palettes, holo, badges, mapping) is untouched.
   hb/hs = head base + INT-scale · tyb/tys = torso half-height base + VIT-scale
   txb/txs = torso half-width base + STR-scale (leanness floor) · tcy = torso center Y
   headOff = head lift · legRb/legRs = leg radius · fierce = default brow/eye aggression */
const PROFILES={
  lean:   {hb:5.7,hs:1.6, tyb:10.8,tys:3.2, txb:6.9,txs:4.7, tcy:31.6, headOff:4.0, legRb:2.7,legRs:0.9, armLong:1.0, fierce:1},
  menace: {hb:5.0,hs:1.4, tyb:11.8,tys:3.4, txb:6.3,txs:4.8, tcy:30.2, headOff:3.4, legRb:2.5,legRs:0.9, armLong:1.18, fierce:2},
};
let PROP=PROFILES.menace;   // MENACING is the shipped default (swappable to lean per call)

/* ---------------- deterministic noise ---------------- */
function mkSeed(g){ return ((g[0]<<24)^(g[7]<<16)^(g[14]<<8)^g[31])>>>0; }
function hash2(x,y,seed){
  let h=(Math.imul(x|0,0x9E3779B1)^Math.imul(y|0,0x85EBCA77)^Math.imul(seed|0,0xC2B2AE3D))>>>0;
  h=Math.imul(h^(h>>>15),0x2C1B3C6D)>>>0; h=Math.imul(h^(h>>>12),0x297A2D39)>>>0;
  return ((h^(h>>>15))>>>0)/4294967296;
}

/* ---------------- color ---------------- */
function hsl(h,s,l){ // h deg, s/l 0..100 -> [r,g,b]
  h=((h%360)+360)%360; s/=100; l/=100;
  const c=(1-Math.abs(2*l-1))*s, x=c*(1-Math.abs((h/60)%2-1)), m=l-c/2;
  let r,g,b;
  if(h<60){r=c;g=x;b=0}else if(h<120){r=x;g=c;b=0}else if(h<180){r=0;g=c;b=x}
  else if(h<240){r=0;g=x;b=c}else if(h<300){r=x;g=0;b=c}else{r=c;g=0;b=x}
  return [Math.round((r+m)*255),Math.round((g+m)*255),Math.round((b+m)*255)];
}
function mix(a,b,t){ return [a[0]+(b[0]-a[0])*t|0, a[1]+(b[1]-a[1])*t|0, a[2]+(b[2]-a[2])*t|0]; }
function hueTo(h,target,k){ let d=((target-h+540)%360)-180; return h+d*k; }
/* 5-step ramp with hue-shifted shadows (toward violet) and highlights (toward warm light) */
function ramp(h,s,l){
  const P=[
    [hueTo(h,268,.34), Math.min(100,s+16), Math.max(4,l*0.36)],
    [hueTo(h,268,.16), Math.min(100,s+8),  l*0.62],
    [h, s, l],
    [hueTo(h,52,.14),  Math.max(0,s-8),  Math.min(96,l*1.28+6)],
    [hueTo(h,52,.26),  Math.max(0,s-22), Math.min(99,l*1.5+16)],
  ];
  return P.map(p=>hsl(p[0],p[1],p[2]));
}

/* ---------------- element palettes (hand-tuned) ---------------- */
const EPAL=[
  { // Ember — crimson beast, gold light
    body:[8,66,44], belly:[30,72,56], accH:[38,100,60],
    bgTop:[16,8,10], bgBot:[52,20,16], glow:[255,120,60], star:[255,170,90] },
  { // Gale — jade wind spirit
    body:[152,30,58], belly:[92,26,74], accH:[168,85,64],
    bgTop:[8,14,14], bgBot:[22,42,36], glow:[120,255,190], star:[190,255,220] },
  { // Terra — moss + amber
    body:[92,34,40], belly:[46,48,58], accH:[38,90,58],
    bgTop:[10,12,7], bgBot:[30,36,18], glow:[190,160,70], star:[220,200,120] },
  { // Tide — deep-sea blue
    body:[212,52,48], belly:[188,42,64], accH:[182,92,62],
    bgTop:[5,10,20], bgBot:[12,36,54], glow:[90,210,255], star:[150,230,255] },
  { // Volt — charcoal + electric yellow
    body:[232,14,36], belly:[50,80,56], accH:[55,98,60],
    bgTop:[10,10,14], bgBot:[34,32,20], glow:[255,235,90], star:[255,245,150] },
  { // Frost — pale ice
    body:[203,44,62], belly:[195,26,80], accH:[186,95,70],
    bgTop:[7,12,24], bgBot:[22,44,66], glow:[160,230,255], star:[210,245,255] },
  { // Radiant — gold-cream
    body:[44,52,60], belly:[48,38,78], accH:[50,100,66],
    bgTop:[20,14,10], bgBot:[62,44,24], glow:[255,225,140], star:[255,240,190] },
  { // Umbral — void violet
    body:[268,36,36], belly:[286,30,50], accH:[305,88,62],
    bgTop:[7,5,14], bgBot:[26,16,44], glow:[220,110,255], star:[240,190,255] },
];
function buildPal(el,palGene){
  const P=EPAL[el];
  const twist=((palGene%5)-2)*6;              // small deterministic hue twist per hero
  return {
    body:ramp(P.body[0]+twist*.4,P.body[1],P.body[2]),
    belly:ramp(P.belly[0]+twist,P.belly[1],P.belly[2]),
    bone:ramp(44,26,72),
    gold:ramp(45,74,56),
    stone:ramp(255,8,40),
    acc:ramp(P.accH[0],P.accH[1],P.accH[2]),
    dark:ramp(P.body[0],P.body[1]*0.6,12),
    bgTop:P.bgTop,bgBot:P.bgBot,glow:P.glow,star:P.star,
  };
}

/* =====================================================================
   PIXEL BUFFERS
   creature buffer stores (material,shade) so markings can recolor while
   preserving shading; bg + fx are plain RGB layers.
   ===================================================================== */
const W=64,H=60;
function mkBuf(){ return { m:new Int16Array(W*H).fill(-1), s:new Int8Array(W*H) }; }
function bset(b,x,y,m,s){ x|=0;y|=0; if(x<0||y<0||x>=W||y>=H)return; const i=y*W+x; b.m[i]=m; b.s[i]=s<0?0:(s>4?4:s|0); }
function bmat(b,x,y){ x|=0;y|=0; return (x<0||y<0||x>=W||y>=H)?-1:b.m[y*W+x]; }

const LX=-0.44, LY=-0.6, LZ=0.67, LN=Math.hypot(LX,LY,LZ);
function shadeFrom(nx,ny,nz,gamma){
  const nl=Math.hypot(nx,ny,nz)||1;
  let t=((nx*LX+ny*LY+nz*LZ)/(nl*LN)+1)/2;
  return Math.pow(t,gamma);
}
function put(b,x,y,m,t,dither){
  let f=t*4.6-0.3, s=Math.floor(f), fr=f-s;
  if(dither && s>=0 && s<2 && fr>0.36 && fr<0.64 && ((x+y)&1)) s+=1;
  bset(b,x,y,m,s);
}
/* solid ellipse with sphere shading */
function sphere(b,cx,cy,rx,ry,m,o={}){
  const flat=o.flat||0, gam=o.gamma||1.12;
  for(let y=Math.floor(cy-ry);y<=Math.ceil(cy+ry);y++)
  for(let x=Math.floor(cx-rx);x<=Math.ceil(cx+rx);x++){
    const nx=(x+0.5-cx)/rx, ny=(y+0.5-cy)/ry, d2=nx*nx+ny*ny;
    if(d2>1)continue;
    let nz=Math.sqrt(Math.max(0,1-d2)); nz=nz*(1-flat)+flat;
    put(b,x,y,m,shadeFrom(nx,ny,nz,gam),o.dither!==false);
  }
}
/* curved tapered limb along quadratic bezier p0..p2 */
function ribbon(b,p0,p1,p2,r0,r1,m,o={}){
  const K=26, pts=[];
  for(let i=0;i<=K;i++){ const t=i/K, u=1-t;
    pts.push([u*u*p0[0]+2*u*t*p1[0]+t*t*p2[0], u*u*p0[1]+2*u*t*p1[1]+t*t*p2[1], r0+(r1-r0)*t]); }
  let x0=1e9,y0=1e9,x1=-1e9,y1=-1e9,rm=Math.max(r0,r1);
  for(const p of pts){ x0=Math.min(x0,p[0]);y0=Math.min(y0,p[1]);x1=Math.max(x1,p[0]);y1=Math.max(y1,p[1]); }
  for(let y=Math.floor(y0-rm);y<=Math.ceil(y1+rm);y++)
  for(let x=Math.floor(x0-rm);x<=Math.ceil(x1+rm);x++){
    let best=1e9,br=1;
    for(const p of pts){ const dx=x+0.5-p[0],dy=y+0.5-p[1],d=dx*dx+dy*dy;
      if(d<best){best=d;br=p[2];} }
    const d=Math.sqrt(best), v=d/Math.max(0.35,br);
    if(v>1)continue;
    let ddx=0,ddy=0;
    if(d>0.001){ for(const p of pts){ const dx=x+0.5-p[0],dy=y+0.5-p[1];
      if(dx*dx+dy*dy===best){ ddx=dx/d*v; ddy=dy/d*v; break; } } }
    const nz=Math.sqrt(Math.max(0,1-v*v));
    put(b,x,y,m,shadeFrom(ddx,ddy,nz,o.gamma||1.1),o.dither!==false);
  }
}
/* bitmap blit: rows of chars; digits map to (mat, shade), custom chars via map */
function blit(b,x0,y0,rows,mat,map={}){
  for(let j=0;j<rows.length;j++)for(let i=0;i<rows[j].length;i++){
    const c=rows[j][i]; if(c==='.'||c===' ')continue;
    if(map[c]) bset(b,x0+i,y0+j,map[c][0],map[c][1]);
    else bset(b,x0+i,y0+j,mat,+c);
  }
}

/* =====================================================================
   RENDER ONE HERO  →  offscreen 64×60 canvas
   ===================================================================== */
function renderHero(hx){
  const h=hero(hx), g=h.g, seed=mkSeed(g);
  const pal=buildPal(h.element,g[15]);
  const affEl=(h.ttier(6)>=0)?(g[28]%8):h.element;
  const affAcc=ramp(EPAL[affEl].accH[0],EPAL[affEl].accH[1],EPAL[affEl].accH[2]);

  /* material table */
  const MATS=[];
  const M=r=>{MATS.push({ramp:r});return MATS.length-1;};
  const BODY=M(pal.body), BELLY=M(pal.belly), BONE=M(pal.bone), GOLD=M(pal.gold),
        STONE=M(pal.stone), ACC=M(pal.acc), AFF=M(affAcc), DARK=M(pal.dark),
        MARK=M(ramp(EPAL[h.element].accH[0],EPAL[h.element].accH[1]*0.8,Math.max(14,EPAL[h.element].body[2]*0.72))),
        MARKG=M(pal.acc.map(c=>mix(c,[255,255,255],0.15))),
        WHITE=M([[24,26,36],[90,96,120],[168,176,200],[228,232,244],[255,255,255]]);

  /* ---- stat-driven anatomy ---- */
  const f=i=>g[i]/255;
  const str=f(0),vit=f(1),agi=f(2),intg=f(3),luk=f(4);
  const title=g[14];
  const stance=h.ttier(5);              // -1 none … 4 legendary
  const lev=stance===4;                 // levitate
  const G=53;                           // ground row
  const cx=32;
  const PR=PROP;                         // active proportion profile (de-chibi axis)
  let trx=(PR.txb+str*PR.txs); trx*=(1-agi*0.12);   // leaner floor; STR still bulks
  const trY=(PR.tyb+vit*PR.tys);         // taller torso
  let tcy=PR.tcy; if(lev)tcy-=4;         // ride higher in frame
  const hr=(PR.hb+intg*PR.hs);           // SMALLER head (adult ratio)
  const headShape=title%3;              // 0 round 1 wide 2 tall
  const hrx=hr*(headShape===1?1.22:headShape===2?0.95:1.08);
  const hry=hr*(headShape===1?0.86:headShape===2?1.08:0.97);
  const hcy=tcy-trY-hry+PR.headOff;
  const legSp=4.8+str*2.2+(stance===1?2.0:stance===0?1:0);
  const armR=2.3+str*1.7;
  const earType=title%4, muzType=(title>>2)%4;
  const athletic=agi>str+0.08;

  const bg=new Float32Array(W*H*3);     // rgb layer
  const setbg=(x,y,c,t=1)=>{ x|=0;y|=0; if(x<0||y<0||x>=W||y>=H)return; const i=(y*W+x)*3;
    bg[i]=bg[i]+(c[0]-bg[i])*t; bg[i+1]=bg[i+1]+(c[1]-bg[i+1])*t; bg[i+2]=bg[i+2]+(c[2]-bg[i+2])*t; };
  const getbg=(x,y)=>{const i=(y*W+x)*3;return [bg[i],bg[i+1],bg[i+2]];};

  /* ================= BACKDROP ================= */
  const bay=[[0,2],[3,1]];
  for(let y=0;y<H;y++)for(let x=0;x<W;x++){
    let t=y/H; t=t*t*(3-2*t);
    const steps=9, ft=t*steps, bt=(Math.floor(ft)+(ft-Math.floor(ft)>(bay[y&1][x&1]+0.5)/4?1:0))/steps;
    setbg(x,y,mix(pal.bgTop,pal.bgBot,bt));
  }
  /* faint moon-disc behind creature */
  for(let y=0;y<H;y++)for(let x=0;x<W;x++){
    const d=Math.hypot((x-cx)/16.5,(y-21)/16.5);
    if(d<1){ const k=(1-d)*0.24+(d>0.86?0.1:0);
      if(d>0.86&&((x+y)&1))continue;
      setbg(x,y,mix(getbg(x,y),pal.glow,k*0.5)); }
  }
  /* horizon glow band for depth */
  for(let y=G-9;y<G;y++)for(let x=0;x<W;x++){
    const k=1-Math.abs(y-(G-5))/5;
    if(((x+y)&1)===0) setbg(x,y,mix(getbg(x,y),pal.glow,k*0.06));
  }
  /* element motif speckles (sparse, away from creature) */
  for(let i=0;i<14;i++){
    const x=(hash2(i,7,seed)*W)|0, y=(hash2(i,13,seed)*(G-8))|0;
    if(Math.abs(x-cx)<14&&y>10)continue;
    setbg(x,y,mix(getbg(x,y),pal.star,0.45));
  }
  /* ground */
  for(let y=G;y<H;y++)for(let x=0;x<W;x++){
    const dk=0.42+(y-G)*0.06;
    setbg(x,y,mix(getbg(x,y),[6,6,10],dk));
    if(y===G&&((x+1)&1)) setbg(x,y,mix(getbg(x,y),pal.glow,0.08));
  }
  /* vignette */
  for(let y=0;y<H;y++)for(let x=0;x<W;x++){
    const d=Math.hypot((x-cx)/40,(y-26)/40);
    if(d>0.72) setbg(x,y,mix(getbg(x,y),[4,4,8],(d-0.72)*1.15));
  }
  /* contact shadow */
  const shR=trx+3;
  for(let y=G;y<=G+3;y++)for(let x=cx-shR|0;x<=cx+shR;x++){
    const d=Math.hypot((x-cx)/shR,(y-(G+1.4))/2.3);
    if(d<1&&(d<0.72||((x+y)&1))) setbg(x,y,mix(getbg(x,y),[3,3,6],lev?0.34:0.62));
  }

  /* ================= AURA (behind creature) ================= */
  const aura=h.ttier(0);
  const glo=(k)=>mix(pal.glow,[255,255,255],k);   // bright aura color
  const sblit=(x0,y0,rows,col)=>{ for(let j=0;j<rows.length;j++)for(let i=0;i<rows[j].length;i++){
    const c=rows[j][i]; if(c==='.')continue;
    setbg(x0+i,y0+j, c==='W'?glo(0.55):pal.glow, c==='W'?1:0.85); } };
  const SPARK4=['..W..','..W..','WWWWW','..W..','..W..'];
  const SPARK2=['.W.','WWW','.W.'];
  if(aura===0){ /* ground mist */
    for(let i=0;i<14;i++){ const x=cx-14+(hash2(i,3,seed)*28|0), y=G-1-(hash2(i,9,seed)*3|0);
      setbg(x,y,mix(getbg(x,y),pal.glow,0.35)); }
  }else if(aura===1){ /* halo ring */
    for(let a=0;a<72;a++){ const th=a/72*Math.PI*2, x=cx+Math.cos(th)*16, y=tcy-3+Math.sin(th)*13.5;
      setbg(x|0,y|0,pal.glow,(a&1)?0.35:0.8); }
  }else if(aura===2){ /* rising particles */
    for(let i=0;i<13;i++){ const x=cx-14+(hash2(i,5,seed)*28|0), y=7+(hash2(i,11,seed)*(G-14)|0);
      setbg(x,y,pal.glow,0.85); setbg(x,y+1,pal.glow,0.35); }
    sblit(cx-16,12,SPARK2); sblit(cx+13,20,SPARK2);
  }else if(aura===3){ /* twin energy arcs */
    for(const s of[-1,1]){
      for(let i=0;i<=22;i++){ const t=i/22,u=1-t;
        const x=u*u*(cx+s*13)+2*u*t*(cx+s*19)+t*t*(cx+s*9);
        const y=u*u*(G-3)+2*u*t*(tcy-4)+t*t*(hcy-9);
        setbg(x|0,y|0,glo(0.2),1); setbg((x+s)|0,y|0,pal.glow,0.55);
        if((i%6)===0)setbg((x-s)|0,(y-1)|0,pal.glow,0.4);
      }
    }
    sblit(cx-19,hcy-6,SPARK2); sblit(cx+17,hcy-6,SPARK2);
  }else if(aura===4){ /* radiant burst + sparkles */
    for(let a=0;a<12;a++){ const th=a/12*Math.PI*2+0.26, len=(a&1)?23:17;
      for(let r=12;r<len;r++){ const x=cx+Math.cos(th)*r, y=tcy-4+Math.sin(th)*r*0.82;
        setbg(x|0,y|0,(r&1)?pal.glow:glo(0.25),1); } }
    for(let y=0;y<H;y++)for(let x=0;x<W;x++){
      const d=Math.hypot((x-cx)/19,(y-(tcy-3))/17);
      if(d<1) setbg(x,y,mix(getbg(x,y),pal.glow,(1-d)*0.2));
    }
    sblit(cx-20,hcy-6,SPARK4); sblit(cx+16,tcy+2,SPARK4); sblit(cx-14,G-6,SPARK2);
  }
  /* legendary sigil = rune ring behind torso */
  if(h.ttier(4)===4){
    for(let a=0;a<88;a++){ const th=a/88*Math.PI*2, x=cx+Math.cos(th)*18, y=tcy-1+Math.sin(th)*13.5;
      setbg(x|0,y|0,glo(0.15),0.95); }
    for(let k=0;k<6;k++){ const th=k/6*Math.PI*2+0.5, x=cx+Math.cos(th)*18|0, y=tcy-1+Math.sin(th)*13.5|0;
      setbg(x,y,glo(0.6));setbg(x+1,y,glo(0.3));setbg(x,y-1,glo(0.3)); }
  }

  /* ================= CREATURE ================= */
  const b=mkBuf();

  /* ---- tail (behind) ---- */
  const tl=8+agi*9;
  ribbon(b,[cx-trx*0.5,tcy+trY-4],[cx-trx-5-tl*0.4,tcy+trY-6],[cx-trx-3-tl*0.6,tcy+trY-13-tl*0.6],2.9,1.0,BODY);
  if(h.ttier(6)>=2){ /* affinity: burning tail tip */
    sphere(b,cx-trx-3-tl*0.6,tcy+trY-13-tl*0.6,2.3,2.7,AFF,{gamma:0.8});
  }

  /* ---- element back-feature (signature silhouette per element) ---- */
  const topY=tcy-trY+1.5;
  for(const s of[-1,1]){
    const bx1=cx+s*trx*0.62, bx2=cx+s*trx*0.92, by2=topY+4;
    switch(h.element){
      case 0: /* Ember: waving flame spikes */
        ribbon(b,[bx1,topY+2],[bx1+s*2,topY-3],[bx1-s*1,topY-7],1.9,0.4,ACC);
        ribbon(b,[bx2,by2+2],[bx2+s*2,by2-2],[bx2+s*0.5,by2-5],1.5,0.4,ACC); break;
      case 1: /* Gale: swept wind fins */
        ribbon(b,[bx1,topY+2],[bx1+s*4,topY-1],[bx1+s*7.5,topY-0.5],2.1,0.4,BELLY);
        ribbon(b,[bx2,by2+1],[bx2+s*3,by2-0.5],[bx2+s*6,by2],1.6,0.4,BELLY); break;
      case 2: /* Terra: rock plates */
        sphere(b,bx1,topY-0.5,2.6,2.9,STONE,{gamma:1.25});
        sphere(b,bx2,by2-0.5,2.1,2.4,STONE,{gamma:1.25}); break;
      case 3: /* Tide: dorsal fins */
        ribbon(b,[bx1,topY+2],[bx1+s*1.5,topY-3.5],[bx1+s*4,topY-6],2.3,0.4,ACC); break;
      case 4: /* Volt: zigzag bolts */
        ribbon(b,[bx1,topY+1],[bx1+s*3,topY-2],[bx1+s*1.5,topY-4],1.6,0.6,ACC);
        ribbon(b,[bx1+s*1.5,topY-4],[bx1+s*4.5,topY-6],[bx1+s*3,topY-8.5],1.2,0.3,ACC); break;
      case 5: /* Frost: ice shards */
        ribbon(b,[bx1,topY+1.5],[bx1+s*1.2,topY-3],[bx1+s*2,topY-7.5],1.9,0.3,ACC);
        ribbon(b,[bx2,by2+1],[bx2+s*1.4,by2-2],[bx2+s*2.2,by2-4.5],1.4,0.3,ACC); break;
      case 6: /* Radiant: gold rays */
        ribbon(b,[bx1,topY+1.5],[bx1+s*1,topY-3],[bx1+s*1.6,topY-6.5],1.5,0.4,GOLD);
        ribbon(b,[bx2,by2+1],[bx2+s*1.6,by2-2],[bx2+s*2.6,by2-4],1.2,0.3,GOLD); break;
      case 7: /* Umbral: shadow wisps */
        ribbon(b,[bx1,topY+2],[bx1+s*2.5,topY-2.5],[bx1-s*0.5,topY-6.5],1.8,0.4,DARK);
        sphere(b,bx1-s*0.5,topY-6.8,1.1,1.1,ACC,{gamma:0.8}); break;
    }
  }

  /* ---- legs (longer + leaner for adult stance) ---- */
  const footY=lev?G-6:G-1;
  for(const s of[-1,1]){
    const hx0=cx+s*legSp, hipY=tcy+trY-5;
    ribbon(b,[hx0,hipY],[hx0+s*2.4,hipY+(footY-hipY)*0.5],[hx0+s*1.2,footY-1],PR.legRb+0.4+vit*PR.legRs-(athletic?0.5:0),PR.legRb-0.1,BODY);
    sphere(b,hx0+s*2,footY,3.2,2.0,BODY,{gamma:1.2});
    bset(b,hx0+s*2-2,footY+1,BONE,3); bset(b,hx0+s*2,footY+1,BONE,4); bset(b,hx0+s*2+2,footY+1,BONE,3);
  }

  /* ---- torso: build varies with the stat genes ---- */
  if(athletic){ /* tapered chest-over-hips */
    sphere(b,cx,tcy+trY*0.38,trx*0.8,trY*0.6,BODY,{flat:0.12});
    sphere(b,cx,tcy-trY*0.26,trx,trY*0.74,BODY,{flat:0.16});
  }else{
    sphere(b,cx,tcy,trx,trY,BODY,{flat:0.16});
  }
  /* bruiser shoulder caps when STR-heavy */
  if(str>0.55){
    for(const s of[-1,1]) sphere(b,cx+s*(trx-1.2),tcy-trY+3.6,2.6+str*1.8,2.4+str*1.6,BODY,{gamma:1.05});
  }
  /* chest plate (high, armor-like, not a diaper) */
  for(let y=Math.floor(tcy-trY*0.5);y<=Math.ceil(tcy+trY*0.66);y++)
  for(let x=Math.floor(cx-trx*0.44);x<=Math.ceil(cx+trx*0.44);x++){
    const nx=(x+0.5-cx)/(trx*0.44), ny=(y+0.5-(tcy+trY*0.08))/(trY*0.56), d2=nx*nx+ny*ny;
    if(d2>1||bmat(b,x,y)!==BODY)continue;
    const nz=Math.sqrt(Math.max(0,1-d2));
    put(b,x,y,BELLY,shadeFrom(nx*0.8,ny*0.8,nz*0.55+0.45,1.3),true);
  }
  /* belly ridges */
  for(let k=0;k<2;k++){ const y=(tcy+trY*0.04+k*2.8)|0;
    for(let x=cx-trx*0.32|0;x<=cx+trx*0.32;x++)
      if(bmat(b,x,y)===BELLY&&bmat(b,x,y+1)===BELLY){ const i=y*W+x; if(b.s[i]>0)b.s[i]-=1; }
  }

  /* ---- arms (longer reach; default flares outward = ready/aggressive) ---- */
  const fists=(stance===1||stance===3);
  const aL=PR.armLong;
  for(const s of[-1,1]){
    const shx=cx+s*(trx-1), shy=tcy-trY+4.5;
    let e,p;
    if(fists){ e=[shx+s*4.6,shy+4.5*aL]; p=[shx+s*5.4,shy+0.5]; }
    else { e=[shx+s*4.3,shy+5.5*aL]; p=[shx+s*5.0,shy+10.5*aL]; }
    ribbon(b,[shx,shy],e,p,armR*0.94,armR*0.8,BODY);
    sphere(b,p[0],p[1],armR*0.98,armR*0.92,BODY,{gamma:1.15});
    /* claws */
    if(fists){ bset(b,p[0]+s,p[1]-1,BONE,4); bset(b,p[0]+s,p[1]+1,BONE,3); }
    else { bset(b,p[0]-1,p[1]+armR*0.5,BONE,4); bset(b,p[0]+1,p[1]+armR*0.5,BONE,3); }
    if(h.ttier(6)===3||h.ttier(6)===4){ /* affinity gauntlets */
      sphere(b,p[0],p[1],armR*1.05,armR*1.0,AFF,{gamma:0.85});
    }
  }

  /* ---- head ---- */
  sphere(b,cx,hcy,hrx,hry,BODY,{flat:0.1});

  /* ---- ears (skip if big crest replaces them) ---- */
  const crest=h.ttier(3);
  if(crest<2){
    for(const s of[-1,1]){
      const ex=cx+s*hrx*0.72, ey=hcy-hry*0.6;
      if(earType===0) ribbon(b,[ex,ey+1.5],[ex+s*1.6,ey-2.5],[ex+s*3,ey-6-agi*2],2.3,0.6,BODY);
      else if(earType===1) sphere(b,ex+s*0.6,ey-1.8,2.7,3.0,BODY);
      else if(earType===2) ribbon(b,[ex,ey+1.5],[ex+s*3.6,ey-1.2],[ex+s*6.2,ey+0.8],2.4,0.5,BODY);
      else ribbon(b,[ex-s*0.4,ey+1.5],[ex+s*1,ey-4],[ex+s*1.8,ey-8.5-agi*2.5],2.4,0.9,BODY);
    }
  }

  /* ---- crest (trait badge on head) ---- */
  if(crest===0){        /* nub horns */
    for(const s of[-1,1]) ribbon(b,[cx+s*hrx*0.5,hcy-hry*0.7],[cx+s*hrx*0.64,hcy-hry*1.0],[cx+s*hrx*0.72,hcy-hry*1.35],1.6,0.4,BONE);
  }else if(crest===1){  /* twin swept horns, glowing tips */
    for(const s of[-1,1]){
      ribbon(b,[cx+s*hrx*0.55,hcy-hry*0.58],[cx+s*hrx*1.2,hcy-hry*1.3],[cx+s*hrx*0.82,hcy-hry*1.9],2.1,0.5,BONE);
      ribbon(b,[cx+s*hrx*1.0,hcy-hry*1.5],[cx+s*hrx*0.94,hcy-hry*1.72],[cx+s*hrx*0.82,hcy-hry*1.9],1.0,0.4,ACC);
    }
  }else if(crest===2){  /* blade fin crest */
    ribbon(b,[cx,hcy-hry*0.7],[cx,hcy-hry*1.4],[cx,hcy-hry*1.95],2.4,0.5,ACC);
    for(const s of[-1,1]) ribbon(b,[cx+s*hrx*0.52,hcy-hry*0.58],[cx+s*hrx*0.9,hcy-hry*1.1],[cx+s*hrx*1.1,hcy-hry*1.5],2.0,0.5,ACC);
  }else if(crest===3){  /* antler rack, glowing tips */
    for(const s of[-1,1]){
      ribbon(b,[cx+s*hrx*0.5,hcy-hry*0.62],[cx+s*hrx*1.28,hcy-hry*1.4],[cx+s*hrx*1.14,hcy-hry*2.0],1.9,0.6,BONE);
      ribbon(b,[cx+s*hrx*1.02,hcy-hry*1.3],[cx+s*hrx*1.72,hcy-hry*1.5],[cx+s*hrx*2.04,hcy-hry*1.82],1.2,0.4,BONE);
      sphere(b,cx+s*hrx*1.14,hcy-hry*2.0,1.3,1.3,ACC,{gamma:0.8});
      sphere(b,cx+s*hrx*2.04,hcy-hry*1.82,1.1,1.1,ACC,{gamma:0.8});
    }
  }else if(crest===4){  /* THE floating crown */
    const cw=[
      '.3..4..3.',
      '.3.444.3.',
      '.3344433.',
      '3g34443g3',
      '333333333',
      '223333322',
    ];
    blit(b,cx-4,Math.max(0,hcy-hry-9),cw,GOLD,{g:[ACC,4]});
  }

  /* ---- face ---- */
  const eyes=h.ttier(2);
  const ex0=Math.round(hrx*0.42), eyy=Math.round(hcy-0.6);
  const E=(x,y,m,s)=>bset(b,x,y,m,s);
  const socket=(x,w,hgt)=>{ for(let j=0;j<hgt;j++)for(let i=0;i<w;i++) E(x+i,eyy-1+j,DARK,0); };
  if(eyes<0){           /* gen-0 default: sharp determined glare (fierce, not cute) */
    for(const s of[-1,1]){ const x=cx+s*ex0;
      /* slanted lid — outer high, inner corner dips toward nose = narrowed glare */
      E(x+s,eyy-1,DARK,0);E(x,eyy-1,DARK,0);
      E(x-s,eyy,DARK,0);
      E(x,eyy,ACC,4);E(x+s,eyy,ACC,3);
      E(x+s,eyy-1,ACC,2);
      E(x,eyy+1,DARK,0);E(x+s,eyy+1,DARK,0);
    }
  }else if(eyes===0){   /* almond glow */
    for(const s of[-1,1]){ const x=cx+s*ex0-1;
      socket(x-1,4,2);
      E(x,eyy,ACC,4);E(x+1,eyy,ACC,3);E(x+(s>0?1:0),eyy-1,WHITE,4);
    }
  }else if(eyes===1){   /* fierce slits */
    for(const s of[-1,1]){ const x=cx+s*ex0-1;
      E(x-1,eyy-1,DARK,0);E(x,eyy-1,DARK,0);E(x+1,eyy-1,DARK,0);E(x+2,eyy-1,DARK,0);
      E(x-1,eyy,DARK,0);E(x,eyy,ACC,4);E(x+1,eyy,ACC,3);E(x+2,eyy,DARK,0);
      E(x,eyy+1,DARK,0);E(x+1,eyy+1,DARK,0);
    }
  }else if(eyes===2){   /* VISOR band */
    const vw=Math.round(hrx*0.78);
    for(let x=cx-vw;x<=cx+vw;x++){ E(x,eyy-2,DARK,0);E(x,eyy-1,DARK,1);E(x,eyy,DARK,1);E(x,eyy+1,DARK,0); }
    for(let x=cx-vw+1;x<=cx+vw-1;x++){ E(x,eyy-1,ACC,((x+seed)&3)===0?4:3); }
    E(cx-vw+1,eyy-1,WHITE,4);
  }else if(eyes===3){   /* blazing */
    for(const s of[-1,1]){ const x=cx+s*ex0;
      socket(x-2,5,4);
      E(x-1,eyy,ACC,4);E(x,eyy,WHITE,4);E(x+1,eyy,WHITE,4);
      E(x-1,eyy+1,ACC,2);E(x,eyy+1,ACC,4);E(x+1,eyy+1,ACC,3);
      E(x+s,eyy-2,ACC,3);E(x+s*2,eyy-3,ACC,1);
    }
  }else{                /* legendary: three burning eyes */
    for(const s of[-1,1]){ const x=cx+s*ex0;
      socket(x-2,5,3);
      E(x-1,eyy,ACC,3);E(x,eyy,WHITE,4);E(x+1,eyy,ACC,3);E(x,eyy+1,ACC,2);
    }
    const fy=Math.round(hcy-hry*0.52);
    E(cx,fy-1,ACC,2);E(cx-1,fy,ACC,3);E(cx,fy,WHITE,4);E(cx+1,fy,ACC,3);E(cx,fy+1,ACC,2);
  }
  /* brow ridge — angled down toward the nose = default scowl (fiercer proportions) */
  const temp=h.ttier(7);
  const fierce=temp>=1||PR.fierce>=1;
  if(eyes!==2){
    for(const s of[-1,1]){ const x=cx+s*ex0;
      E(x+s,eyy-2,BODY,1);E(x,eyy-2,BODY,0);   /* outer high + hard shadow */
      E(x-s,eyy-1,BODY,0);                      /* inner corner dips (angry V) */
      if(PR.fierce>=2||fierce)E(x+s,eyy-3,BODY,1);  /* heavier ridge */
    }
  }

  /* ---- muzzle + mouth (temperament) ---- */
  const my=hcy+hry*0.46;
  if(muzType===1) sphere(b,cx,my,hrx*0.5,hry*0.32,BELLY,{gamma:1.05});
  else if(muzType===2){ bset(b,cx-1,my-1|0,DARK,1); bset(b,cx+1,my-1|0,DARK,1); } /* nostril dots */
  else if(muzType===3){ sphere(b,cx,my+0.6,hrx*0.6,hry*0.34,BELLY,{gamma:1.15});
    bset(b,cx-hrx*0.42|0,my|0,WHITE,4); bset(b,cx-hrx*0.42|0,my-1|0,WHITE,3);
    bset(b,cx+hrx*0.42|0,my|0,WHITE,4); bset(b,cx+hrx*0.42|0,my-1|0,WHITE,3); }
  const mouthY=Math.round(my+(muzType===1?0.6:1.4));
  if(temp<0){ bset(b,cx-1,mouthY,DARK,0);bset(b,cx,mouthY,DARK,0); }
  else if(temp===0){ bset(b,cx-1,mouthY,DARK,0);bset(b,cx,mouthY,DARK,0);bset(b,cx+1,mouthY,DARK,0);bset(b,cx+2,mouthY-1,DARK,0); }
  else if(temp===1){ for(let x=cx-2;x<=cx+2;x++)bset(b,x,mouthY,DARK,0);
    bset(b,cx-1,mouthY+1,WHITE,4);bset(b,cx+1,mouthY+1,WHITE,4); }
  else if(temp===2){ for(let x=cx-3;x<=cx+3;x++){bset(b,x,mouthY,DARK,0);}
    for(let x=cx-2;x<=cx+2;x+=2)bset(b,x,mouthY+1,WHITE,3); }
  else{ /* epic roar / legendary breath: wide open maw */
    const mw=[
      '.11111.',
      '3101013',
      '0000000',
      '.30303.',
      '..111..',
    ];
    blit(b,cx-3,mouthY-1,mw,DARK,{3:[WHITE,4],1:[DARK,1],0:[DARK,0]});
    bset(b,cx,mouthY+1,ACC,3);bset(b,cx-1,mouthY+1,ACC,2);bset(b,cx+1,mouthY+1,ACC,2);
  }

  /* ---- markings (recolor body, keep shade) ---- */
  const mark=h.ttier(1);
  const remap=(cond)=>{ for(let y=0;y<H;y++)for(let x=0;x<W;x++){
    const i=y*W+x; if(b.m[i]!==BODY)continue; const r=cond(x,y); if(r)b.m[i]=r; } };
  if(mark===0){ /* shoulder patches */
    remap((x,y)=>{ const d1=Math.hypot((x-(cx-trx*0.72))/3.6,(y-(tcy-trY*0.4))/4.4);
      const d2=Math.hypot((x-(cx+trx*0.72))/3.6,(y-(tcy-trY*0.4))/4.4);
      return (d1<1||d2<1)?MARK:0; });
  }else if(mark===1){ /* chevron stripes */
    remap((x,y)=>{ if(y<tcy-trY+2||y>tcy+trY-1)return 0;
      const k=Math.abs(x-cx); const band=Math.floor((y-tcy+k*0.45)/3.1);
      return (((band%2)+2)%2===0&&k>trx*0.2&&k<trx*0.99)?MARK:0; });
  }else if(mark===2){ /* rosette rings, deliberately placed */
    const spots=[[cx-trx*0.68,tcy-trY*0.2],[cx+trx*0.68,tcy+trY*0.02],[cx-trx*0.6,tcy+trY*0.42],
                 [cx+trx*0.58,tcy-trY*0.45],[cx-hrx*0.55,hcy-hry*0.32],[cx+hrx*0.52,hcy-hry*0.05]];
    for(const [sx,sy] of spots){
      const x0=Math.round(sx),y0=Math.round(sy);
      for(const [dx,dy] of[[-1,-1],[0,-2],[1,-1],[2,0],[1,1],[0,2],[-1,1],[-2,0]])
        if(bmat(b,x0+dx,y0+dy)===BODY){const i=(y0+dy)*W+x0+dx;b.m[i]=MARK;}
    }
  }else if(mark===3){ /* glowing circuit seams */
    remap((x,y)=>{ const k=Math.abs(x-cx);
      const on=(k===Math.round(trx*0.55)&&y>tcy-trY+3&&y<tcy+trY-1)||
               (y===Math.round(tcy-trY*0.12)&&k<trx*0.85&&k>trx*0.22)||
               (k===Math.round(hrx*0.82)&&y>hcy-1&&y<hcy+4);
      return on?MARKG:0; });
  }else if(mark===4){ /* constellation: deliberate stars */
    const stars=[[cx-trx*0.55,tcy-trY*0.3],[cx+trx*0.5,tcy+trY*0.1],[cx-trx*0.2,tcy+trY*0.55],[cx+trx*0.25,tcy-trY*0.55]];
    for(const [sx,sy] of stars){
      const x=sx|0,y=sy|0;
      if(bmat(b,x,y)!==BODY)continue;
      for(const [dx,dy] of[[0,0],[1,0],[-1,0],[0,1],[0,-1]])
        if(bmat(b,x+dx,y+dy)===BODY){const i=(y+dy)*W+x+dx;b.m[i]=MARKG;}
      const i=y*W+x; b.s[i]=4;
    }
  }

  /* ---- chest sigil ---- */
  const sig=h.ttier(4);
  if(sig===0){ bset(b,cx,tcy+1,MARKG,3);bset(b,cx-1,tcy+1,MARKG,2);bset(b,cx+1,tcy+1,MARKG,2);
    bset(b,cx,tcy,MARKG,2);bset(b,cx,tcy+2,MARKG,2); }
  else if(sig>=1&&sig<=3){
    const rune=[['.2.','232','.2.','232','.2.'],['22.','.23','232','32.','.22'],['2.2','232','.3.','232','2.2']][g[24]%3];
    blit(b,cx-1,tcy-1,rune,MARKG);
  }

  /* =========== pixel-art passes =========== */
  /* ambient occlusion at ground contact */
  for(let y=footY-1;y<=footY+2;y++)for(let x=0;x<W;x++){
    if(y<0||y>=H)continue;
    const i=y*W+x; if(b.m[i]>=0&&b.s[i]>0)b.s[i]-=1;
  }
  /* selective inner outline: darken bottom edges */
  const filled=(x,y)=>bmat(b,x,y)>=0;
  const sn=b.s.slice();
  for(let y=0;y<H;y++)for(let x=0;x<W;x++){
    if(!filled(x,y))continue; const i=y*W+x;
    if(!filled(x,y+1)) sn[i]=Math.max(0,b.s[i]-2);
    else if(!filled(x-1,y)) sn[i]=Math.max(0,b.s[i]-1);
  }
  b.s.set(sn);
  /* clean solid rim light on shadow side of head + torso */
  const rimCol=M(pal.acc.map(c=>mix(c,[255,255,255],0.25)));
  for(let y=0;y<H;y++)for(let x=0;x<W;x++){
    if(!filled(x,y))continue;
    const i=y*W+x, m=b.m[i];
    if(m!==BODY&&m!==BELLY&&m!==MARK)continue;
    const inHead=y>hcy-hry&&y<hcy+hry*0.7, inTorso=y>tcy-trY+1&&y<tcy+trY*0.6;
    if(!inHead&&!inTorso)continue;
    if(!filled(x+1,y)&&x>cx+2&&b.s[i]<=1){ b.m[i]=rimCol; b.s[i]=3; }
  }
  /* outer contour, hue-tinted near-black */
  const oline=new Int32Array(W*H).fill(-1);
  for(let y=0;y<H;y++)for(let x=0;x<W;x++){
    if(filled(x,y))continue;
    let nb=-1;
    if(filled(x,y-1))nb=b.m[(y-1)*W+x]; else if(filled(x,y+1))nb=b.m[(y+1)*W+x];
    else if(filled(x-1,y))nb=b.m[y*W+x-1]; else if(filled(x+1,y))nb=b.m[y*W+x+1];
    if(nb<0)continue;
    const c=mix(MATS[nb].ramp[0],[3,3,7],0.62);
    oline[y*W+x]=(c[0]<<16)|(c[1]<<8)|c[2];
  }

  /* ================= FX (in front) ================= */
  const fx=[];
  const F=(x,y,c,a=1)=>fx.push({x:x|0,y:y|0,c,a});
  /* levitation ground ring */
  if(lev){ for(let a=0;a<44;a++){ const th=a/44*Math.PI*2;
    F(cx+Math.cos(th)*10,G+1+Math.sin(th)*1.8,mix(pal.glow,[255,255,255],0.2),(a&1)?0.5:0.9); } }
  /* stance epic: floating shards */
  if(stance===3){ for(const s of[-1,1]){
    F(cx+s*9,G-2,mix(pal.stone[2],pal.glow,0.15),1); F(cx+s*9+1,G-2,pal.stone[3],1);
    F(cx+s*9,G-1,pal.stone[1],1); F(cx+s*12,G-4,pal.stone[2],1); } }
  /* stance rare: stone dais */
  if(stance===2){
    for(let x=cx-12;x<=cx+12;x++){ F(x,G,pal.stone[3],1); F(x,G+1,pal.stone[2],1); F(x,G+2,pal.stone[1],1); }
    for(let x=cx-10;x<=cx+10;x+=4) F(x,G+1,pal.stone[1],1);
    F(cx-12,G,pal.stone[2],1);F(cx+12,G,pal.stone[2],1);
    F(cx-13,G+1,pal.stone[1],1);F(cx+13,G+1,pal.stone[1],1);
  }
  /* affinity motes */
  const affT=h.ttier(6);
  if(affT>=1){ const n=affT===1?2:affT===2?3:5;
    for(let i=0;i<n;i++){ const th=i/n*Math.PI*2+(seed%7)*0.4;
      const x=cx+Math.cos(th)*(trx+7), y=tcy-2+Math.sin(th)*(trY+5)*0.75;
      F(x,y,[255,255,255],1); F(x-1,y,affAcc[3],0.9);F(x+1,y,affAcc[3],0.9);F(x,y-1,affAcc[3],0.9);F(x,y+1,affAcc[2],0.9);
      if(affT>=2)F(x+1,y+1,affAcc[1],0.7);
    } }
  /* legendary breath wisp */
  if(temp===4){ const bx=cx+3,by=hcy+hry*0.5;
    F(bx,by,pal.acc[3],0.9);F(bx+1,by-1,pal.acc[2],0.8);F(bx+2,by-1,pal.acc[3],0.7);F(bx+3,by-2,pal.acc[1],0.6); }
  /* legendary crown sparkle */
  if(crest===4){ const cyT=Math.max(1,hcy-hry-8);
    F(cx+5,cyT,[255,255,255],1);F(cx+5,cyT-1,pal.gold[4],0.7);
    F(cx+4,cyT,pal.gold[4],0.6);F(cx+6,cyT,pal.gold[4],0.6); }
  /* luck dust (subtle) */
  const dust=Math.round(luk*2);
  for(let i=0;i<dust;i++){ const x=(hash2(i,41,seed)*W)|0,y=(hash2(i,43,seed)*(G-10))|0;
    F(x,y,[255,255,255],0.5); }

  /* ================= COMPOSE ================= */
  const cnv=document.createElement('canvas'); cnv.width=W; cnv.height=H;
  const ctx=cnv.getContext('2d'); const img=ctx.createImageData(W,H);
  for(let y=0;y<H;y++)for(let x=0;x<W;x++){
    const i=y*W+x, o=i*4;
    let c;
    if(b.m[i]>=0) c=MATS[b.m[i]].ramp[b.s[i]];
    else if(oline[i]>=0) c=[(oline[i]>>16)&255,(oline[i]>>8)&255,oline[i]&255];
    else c=getbg(x,y);
    img.data[o]=c[0];img.data[o+1]=c[1];img.data[o+2]=c[2];img.data[o+3]=255;
  }
  for(const p of fx){ if(p.x<0||p.y<0||p.x>=W||p.y>=H)continue; const o=(p.y*W+p.x)*4;
    img.data[o]=img.data[o]+(p.c[0]-img.data[o])*p.a;
    img.data[o+1]=img.data[o+1]+(p.c[1]-img.data[o+1])*p.a;
    img.data[o+2]=img.data[o+2]+(p.c[2]-img.data[o+2])*p.a;
  }
  ctx.putImageData(img,0,0);
  return {cnv,h,pal};
}

/* =====================================================================
   Blazor entry points (on top of the ported renderer above).
   ===================================================================== */
const TITLE=['WARDEN','REAVER','ORACLE','SENTINEL','STRIKER','HERALD','STALKER','KEEPER',
  'WARLORD','SAGE','HUNTER','GOLEM','KNIGHT','SHAMAN','RONIN','MARSHAL'];

/** Render a genome to a PNG data URL of the creature + element backdrop (a raw
 *  64×60 image; the <img> upscales it crisply via image-rendering:pixelated).
 *  `size` is advisory (the caller sizes the <img>). `profile` = 'menace' (default) | 'lean'. */
export function renderHeroDataUrl(hex, size, profile){
  PROP = PROFILES[profile] || PROFILES.menace;
  const { cnv } = renderHero(hex);
  return cnv.toDataURL('image/png');
}

/** Genome-derived card chrome (tier/score/element/class/expressed-with-form) for /studio. */
export function heroMeta(hex){
  const h = hero(hex), g = h.g;
  const score = h.expressed.reduce((a,t)=>a+TIER_WEIGHT[t.tier],0);
  return {
    tier: h.tier, tierName: TIERNAME[h.tier], score,
    element: ELEMENTS[h.element], className: TITLE[g[14]%16], gen0: h.gen0,
    expressed: h.expressed.map(t=>({ cat:t.cat, catName:CATNAME[t.cat], tier:t.tier, form:g[16+t.cat*2+1]%3 }))
  };
}
