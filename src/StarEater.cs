using System.Collections.Generic;
using UnityEngine;

// STAR EATER — top-down 3D twin-stick wave survivor (Vampire Survivors-like), but built around a
// completely different core than NEON SWARM (ranged auto-fire + XP orbs + pause/draft cards):
//
//   * NO bullets, NO aiming.  Your only weapon is a blazing HEAT CORONA (an aura disc) that melts
//     any cold VOID shard standing inside it.  Positioning IS attacking: you must physically HUG
//     the swarm to burn it — but drift too deep and the shards TOUCH your core (contact damage).
//     The skill is skimming the swarm's edge: close enough to melt, not so close you're mobbed.
//   * NO menus, NO level-up pause.  Growth is CONTINUOUS: every kill feeds FUSION; fill it and the
//     star hits the next STAR STAGE on the fly (bigger/hotter corona, +orbiting solar flare, +HP).
//   * The hero beat is the SUPERNOVA: burning shards charges an OVERHEAT meter; at 100% the star
//     auto-detonates a sweeping shock-ring that incinerates weak shards, knocks back the rest and
//     grants a brief invuln — a rhythmic "clear the board" catharsis instead of a draft screen.
//   * Warm-vs-cold palette (white-hot star + orange heat pool vs icy-violet crystalline void) so
//     the melt zone reads instantly, opposite of NEON SWARM's cyan grid.
//
// 100% code-generated so it renders reliably in WebGL with engine-code stripping disabled:
//   NO Rigidbody / NO colliders anywhere — player, shards, flares, novas are pure Transform-driven
//   and every interaction is a distance test (coin-cruiser lesson). Juice provides strip-safe FX.
//   Default scene camera/light are stripped and rebuilt so we never double-light or shoot the
//   wrong camera (AutoShot reads Camera.main).
public class StarEater : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__StarEater");
        go.AddComponent<StarEater>();
        DontDestroyOnLoad(go);
    }

    // ---- arena / tuning ----
    const float ARENA_R    = 40f;
    const float PLAYER_R   = 0.72f;   // core contact radius
    const float MOVE_BASE  = 8.6f;
    const float SPAWN_RING = 24f;     // shards appear just offscreen
    const float MAX_DT     = 0.05f;
    const int   MAX_ENEMY  = 170;

    enum State { Playing, Over }
    State state = State.Playing;

    // ---------------------------------------------------------------- entities
    class Shard
    {
        public Transform tr; public int type; public float hp, maxhp, r, speed, dmg, val, resist;
        public float flash, spin, bob; public Vector3 baseScale;
    }
    class Flare { public Transform tr; }
    class Nova  { public Transform ring; public float r, maxR, life, maxLife; public HashSet<Shard> hit = new HashSet<Shard>(); }
    class Mote  { public Transform tr; public int kind; public float bob, life; public Material mat; }  // plasma pickup

    readonly List<Shard> shards = new List<Shard>();
    readonly List<Flare> flares = new List<Flare>();
    readonly List<Nova>  novas  = new List<Nova>();
    readonly List<Mote>  motes  = new List<Mote>();

    // ---------------------------------------------------------------- scene refs
    Transform player, starVisual, flareRoot, auraQuad;
    Light starLight;
    Transform cam; Camera camComp;
    TextMesh hudScore, hudStat, comboText, banner, dbg, hint;
    Transform chBack, chFill, fuBack, fuFill, hpBack, hpFill, flashQuad, tintQuad;
    Material shardMat0, shardMat1, shardMat2, flareMat, starCoreMat, starShellMat;
    Texture2D auraTex;

    // ---------------------------------------------------------------- run state
    float runTime;
    int score, best, kills, stage;
    float hp = 100f, maxHP = 100f;

    // star / weapon
    float auraR = 3.3f, auraDps = 24f;
    float fusion, fusionNeed = 7f;
    float charge, chargeMax = 100f;

    // combo (HEAT)
    int combo; float comboTimer; float heatMult = 1f; bool meltdown; float comboFlash;

    // invuln after supernova
    float invuln;

    // spawn director
    float spawnTimer, spawnInterval = 1.0f;

    // input
    bool ptrDown, ptrWasDown; Vector2 ptrStart, ptrCur; Vector3 lastMoveDir = Vector3.forward;
    bool attract = true; float attractTimer; Vector3 attractDir = Vector3.forward;

    Vector3 camVel;
    float halfH = 6f, halfW = 9f, hudScale = 1f;
    float chY, chW, chH, fuY, fuW, fuH, hpY, hpW, hpH;
    const float HUD_Z = 7f, FOV = 46f;
    bool showDbg; float fps; float damageFlash; float auraPulse;

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);
        foreach (var mr in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            if (mr.gameObject.name == "Cube" || mr.gameObject.name == "Sphere") Destroy(mr.gameObject);

        best = PlayerPrefs.GetInt("stareater_best", 0);

        BuildEnvironment();
        BuildArena();
        BuildMaterials();
        BuildPlayer();
        BuildCamera();
        BuildHud();
        ResetRun();
    }

    // ===================================================================== materials
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.4f, bool emissive = false, float emi = 0.7f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * emi);
        }
        return m;
    }

    void BuildMaterials()
    {
        // cold void shards: icy blues / violets (contrast the warm star)
        shardMat0 = Mat(new Color(0.45f, 0.7f, 1f),  0.1f, 0.55f, true, 0.7f);   // mote  (ice blue)
        shardMat1 = Mat(new Color(0.75f, 0.55f, 1f), 0.1f, 0.6f,  true, 0.75f);  // runner(violet)
        shardMat2 = Mat(new Color(0.35f, 0.45f, 0.9f),0.2f, 0.45f, true, 0.6f);  // hulk  (deep frost)
        flareMat  = MatUnlit(new Color(1f, 0.72f, 0.28f));                       // solar flare (orange, glows)
    }

    // Unlit full-bright material — renders its color regardless of scene lighting. Used for the
    // things that must GLOW (star, flares, nova ring, boundary) since runtime _EmissionColor does
    // not render without a Bloom post-process in this URP setup. Built on Sprites/Default because
    // it is GUARANTEED present in the WebGL build (URP/Unlit gets stripped -> Shader.Find null).
    static Material MatUnlit(Color c)
    {
        var sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
        var m = new Material(sh) { color = c };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        return m;
    }

    static void NoCollide(GameObject g) { var c = g.GetComponent<Collider>(); if (c) Destroy(c); }

    // ===================================================================== environment
    void BuildEnvironment()
    {
        // dim cold key light for form; the STAR itself is the warm hero light (point light below)
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(0.55f, 0.62f, 0.85f);
        sun.intensity = 0.5f;
        sun.transform.rotation = Quaternion.Euler(60f, 22f, 0f);
        sun.shadows = LightShadows.None;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.10f, 0.13f, 0.24f);
        RenderSettings.ambientEquatorColor = new Color(0.06f, 0.07f, 0.15f);
        RenderSettings.ambientGroundColor  = new Color(0.02f, 0.02f, 0.05f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.02f, 0.02f, 0.055f);
        RenderSettings.fogStartDistance = 26f;
        RenderSettings.fogEndDistance = 64f;
    }

    void BuildArena()
    {
        var grid = MakeGridTex();
        var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(floor);
        floor.name = "Floor";
        floor.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        floor.transform.localScale = Vector3.one * (ARENA_R * 2.3f);
        var fm = Mat(new Color(0.05f, 0.06f, 0.11f), 0f, 0.15f, true, 0.9f);
        if (fm.HasProperty("_BaseMap")) fm.SetTexture("_BaseMap", grid);
        if (fm.HasProperty("_MainTex")) fm.SetTexture("_MainTex", grid);
        fm.mainTextureScale = new Vector2(ARENA_R * 0.5f, ARENA_R * 0.5f);
        if (fm.HasProperty("_EmissionMap")) fm.SetTexture("_EmissionMap", grid);
        if (fm.HasProperty("_EmissionColor")) fm.SetColor("_EmissionColor", new Color(0.20f, 0.22f, 0.42f));
        floor.GetComponent<Renderer>().sharedMaterial = fm;

        // glowing event-horizon boundary ring
        var ring = new GameObject("Boundary");
        ring.AddComponent<MeshFilter>().sharedMesh = RingMesh(ARENA_R - 0.6f, ARENA_R + 0.9f, 96);
        ring.AddComponent<MeshRenderer>().sharedMaterial = MatUnlit(new Color(1f, 0.5f, 0.2f));
        ring.transform.position = new Vector3(0f, 0.04f, 0f);
        var ring2 = new GameObject("Boundary2");
        ring2.AddComponent<MeshFilter>().sharedMesh = RingMesh(ARENA_R - 3f, ARENA_R - 2.7f, 96);
        ring2.AddComponent<MeshRenderer>().sharedMaterial = MatUnlit(new Color(0.5f, 0.35f, 0.7f));
        ring2.transform.position = new Vector3(0f, 0.03f, 0f);
    }

    static Texture2D MakeGridTex()
    {
        int S = 128; var t = new Texture2D(S, S);
        var bg = new Color(0.045f, 0.05f, 0.10f);
        var line = new Color(0.16f, 0.18f, 0.34f);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                bool g = (x == 0 || y == 0 || x == 1 || y == 1);
                t.SetPixel(x, y, g ? line : bg);
            }
        t.Apply(); t.wrapMode = TextureWrapMode.Repeat; t.filterMode = FilterMode.Bilinear;
        return t;
    }

    // radial glow texture: bright center -> transparent edge (for the heat aura pool)
    static Texture2D MakeRadialTex()
    {
        int S = 128; var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
        Vector2 c = new Vector2(S * 0.5f, S * 0.5f);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c) / (S * 0.5f);
                float a = Mathf.Clamp01(1f - d);
                a = a * a;                              // soft falloff
                // ring-ish rim to read as a heat pool edge
                float rim = Mathf.Exp(-((d - 0.86f) * (d - 0.86f)) / 0.006f) * 0.5f;
                float v = Mathf.Clamp01(a + rim);
                t.SetPixel(x, y, new Color(1f, 1f, 1f, v));
            }
        t.Apply(); t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
        return t;
    }

    static Mesh RingMesh(float inner, float outer, int seg)
    {
        var v = new Vector3[seg * 2]; var tri = new int[seg * 6];
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg, c = Mathf.Cos(a), s = Mathf.Sin(a);
            v[i * 2]     = new Vector3(c * outer, 0f, s * outer);
            v[i * 2 + 1] = new Vector3(c * inner, 0f, s * inner);
        }
        for (int i = 0; i < seg; i++)
        {
            int a = i * 2, b = ((i + 1) % seg) * 2, t = i * 6;
            tri[t] = a; tri[t + 1] = a + 1; tri[t + 2] = b;
            tri[t + 3] = b; tri[t + 4] = a + 1; tri[t + 5] = b + 1;
        }
        var m = new Mesh(); m.vertices = v; m.triangles = tri; m.RecalculateNormals(); m.RecalculateBounds();
        return m;
    }

    // ===================================================================== player star
    void BuildPlayer()
    {
        player = new GameObject("Player").transform;
        starVisual = new GameObject("StarVisual").transform;
        starVisual.SetParent(player, false);

        // molten outer sun (orange, bright) — drawn first / larger
        var shell = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        NoCollide(shell); shell.transform.SetParent(starVisual, false);
        shell.transform.localScale = Vector3.one * 1.55f;
        starShellMat = MatUnlit(new Color(1f, 0.5f, 0.13f));
        shell.GetComponent<Renderer>().sharedMaterial = starShellMat;
        shell.name = "Shell";

        // white-hot inner core, poking through — brilliant
        var core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        NoCollide(core); core.transform.SetParent(starVisual, false);
        core.transform.localScale = Vector3.one * 1.12f;
        core.transform.localPosition = new Vector3(0f, 0.35f, -0.1f);
        starCoreMat = MatUnlit(new Color(1f, 0.95f, 0.82f));
        core.GetComponent<Renderer>().sharedMaterial = starCoreMat;

        player.position = new Vector3(0f, 0.75f, 0f);

        // warm hero point light emitted by the star
        var lgo = new GameObject("StarLight");
        lgo.transform.SetParent(player, false);
        starLight = lgo.AddComponent<Light>();
        starLight.type = LightType.Point;
        starLight.color = new Color(1f, 0.7f, 0.35f);
        starLight.intensity = 3.2f;
        starLight.range = 16f;
        starLight.shadows = LightShadows.None;

        flareRoot = new GameObject("FlareRoot").transform;
        flareRoot.SetParent(player, false);

        // heat aura pool (flat radial-glow quad on the ground)
        auraTex = MakeRadialTex();
        var aq = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(aq);
        var ash = Shader.Find("Sprites/Default"); if (ash == null) ash = Shader.Find("Unlit/Transparent");
        var am = new Material(ash) { color = new Color(1f, 0.55f, 0.2f, 0.5f) };
        am.mainTexture = auraTex;
        aq.GetComponent<Renderer>().sharedMaterial = am;
        aq.transform.SetParent(player, false);
        aq.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        aq.transform.localPosition = new Vector3(0f, -0.7f, 0f);   // on the floor
        auraQuad = aq.transform;
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.015f, 0.02f, 0.045f);
        camComp.fieldOfView = FOV;
        camComp.farClipPlane = 120f;
        cgo.AddComponent<AudioListener>();
        cam = cgo.transform;
        cam.position = new Vector3(0f, 20f, -12f);
        cam.rotation = Quaternion.Euler(58f, 0f, 0f);
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor, TextAlignment align = TextAlignment.Center)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = align;
        t.transform.SetParent(cam, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    Transform MakeBar(Color c, float emi)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(q);
        q.GetComponent<Renderer>().sharedMaterial = Mat(c, 0f, 0.5f, true, emi);
        q.transform.SetParent(cam, false);
        q.transform.localRotation = Quaternion.identity;
        return q.transform;
    }

    void BuildHud()
    {
        hudScore = MakeText(0.085f, Color.white, TextAnchor.UpperLeft, TextAlignment.Left);
        hudStat  = MakeText(0.060f, new Color(1f, 0.85f, 0.7f), TextAnchor.UpperRight, TextAlignment.Right);
        comboText = MakeText(0.14f, new Color(1f, 0.7f, 0.3f), TextAnchor.MiddleCenter);
        banner   = MakeText(0.13f, Color.white, TextAnchor.MiddleCenter);
        hint     = MakeText(0.055f, new Color(1f, 0.9f, 0.8f), TextAnchor.MiddleCenter);
        dbg      = MakeText(0.040f, new Color(1f, 0.85f, 0.6f), TextAnchor.LowerLeft, TextAlignment.Left);
        dbg.gameObject.SetActive(false);

        chBack = MakeBar(new Color(0.22f, 0.09f, 0.03f), 0.2f);   // supernova charge back
        chFill = MakeBar(new Color(1f, 0.55f, 0.15f), 0.9f);      // charge fill (orange)
        fuBack = MakeBar(new Color(0.08f, 0.06f, 0.16f), 0.2f);   // fusion back
        fuFill = MakeBar(new Color(0.7f, 0.55f, 1f), 0.7f);       // fusion fill (violet)
        hpBack = MakeBar(new Color(0.2f, 0.04f, 0.06f), 0.2f);
        hpFill = MakeBar(new Color(1f, 0.3f, 0.35f), 0.7f);

        // full-screen damage flash (red) behind HUD text
        var f = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(f);
        var fsh = Shader.Find("Sprites/Default"); if (fsh == null) fsh = Shader.Find("Unlit/Transparent");
        f.GetComponent<Renderer>().sharedMaterial = new Material(fsh) { color = new Color(1f, 0.15f, 0.12f, 0f) };
        f.transform.SetParent(cam, false);
        f.transform.localPosition = new Vector3(0f, 0f, HUD_Z + 1.5f);
        f.transform.localScale = new Vector3(90f, 60f, 1f);
        flashQuad = f.transform; SetAlpha(flashQuad, 0f);

        // meltdown / supernova warm tint (also behind HUD)
        var tq = GameObject.CreatePrimitive(PrimitiveType.Quad);
        NoCollide(tq);
        tq.GetComponent<Renderer>().sharedMaterial = new Material(fsh) { color = new Color(1f, 0.5f, 0.15f, 0f) };
        tq.transform.SetParent(cam, false);
        tq.transform.localPosition = new Vector3(0f, 0f, HUD_Z + 1.4f);
        tq.transform.localScale = new Vector3(90f, 60f, 1f);
        tintQuad = tq.transform; SetAlpha(tintQuad, 0f);

        comboText.text = ""; banner.text = "";
        AdjustHud();
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.35f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF = 7.0f;
        hudScale = Mathf.Clamp(halfW / REF, 0.34f, 1.25f);
        float ix = halfW * 0.95f;
        bool portrait = aspect < 0.95f;

        // top: supernova charge strip, then a thinner fusion strip below it
        chW = halfW * 1.94f; chH = halfH * 0.034f; chY = halfH * 0.962f;
        fuW = halfW * 1.5f;  fuH = halfH * 0.020f; fuY = halfH * 0.905f;
        hpW = halfW * 1.5f;  hpH = halfH * 0.038f; hpY = -halfH * 0.955f;
        PlaceBar(chBack, 0f, chY, chW, chH);
        PlaceBar(fuBack, 0f, fuY, fuW, fuH);
        PlaceBar(hpBack, 0f, hpY, hpW, hpH);

        float topY = halfH * 0.80f;
        hudScore.transform.localPosition = new Vector3(-ix, topY, HUD_Z); hudScore.characterSize = 0.078f * hudScale;
        hudStat.transform.localPosition  = new Vector3( ix, topY, HUD_Z); hudStat.characterSize  = 0.054f * hudScale;
        dbg.transform.localPosition      = new Vector3(-ix, -halfH * 0.42f, HUD_Z); dbg.characterSize = 0.040f * hudScale;
        comboText.transform.localPosition = new Vector3(0f, halfH * 0.33f, HUD_Z);
        // gameplay banners (STAGE UP / SUPERNOVA) sit high; the multi-line game-over card centers lower
        float bannerY = (state == State.Over) ? -halfH * 0.06f : halfH * 0.10f;
        banner.transform.localPosition    = new Vector3(0f, bannerY, HUD_Z); banner.characterSize = (portrait ? 0.086f : 0.10f) * hudScale;
        hint.transform.localPosition       = new Vector3(0f, -halfH * 0.6f, HUD_Z); hint.characterSize = 0.05f * hudScale;
    }

    void PlaceBar(Transform b, float cx, float cy, float w, float h)
    {
        b.localPosition = new Vector3(cx, cy, HUD_Z);
        b.localScale = new Vector3(w, h, 1f);
    }

    void RefreshBars()
    {
        float cFrac = Mathf.Clamp01(charge / chargeMax);
        float fFrac = Mathf.Clamp01(fusion / fusionNeed);
        float hFrac = Mathf.Clamp01(hp / maxHP);
        chFill.localScale = new Vector3(chW * cFrac, chH * 0.82f, 1f);
        chFill.localPosition = new Vector3(-chW * 0.5f + chW * cFrac * 0.5f, chY, HUD_Z - 0.02f);
        fuFill.localScale = new Vector3(fuW * fFrac, fuH * 0.82f, 1f);
        fuFill.localPosition = new Vector3(-fuW * 0.5f + fuW * fFrac * 0.5f, fuY, HUD_Z - 0.02f);
        hpFill.localScale = new Vector3(hpW * hFrac, hpH * 0.85f, 1f);
        hpFill.localPosition = new Vector3(-hpW * 0.5f + hpW * hFrac * 0.5f, hpY, HUD_Z - 0.02f);
        // charge bar glows white as it nears full
        var cm = chFill.GetComponent<Renderer>().sharedMaterial;
        Color cc = Color.Lerp(new Color(1f, 0.55f, 0.15f), new Color(1f, 1f, 0.9f), cFrac * cFrac);
        if (cm.HasProperty("_EmissionColor")) cm.SetColor("_EmissionColor", cc * (0.9f + cFrac));
    }

    static void SetBase(Material m, Color c)
    {
        if (m == null) return;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

    void SetAlpha(Transform t, float a)
    {
        var r = t.GetComponent<Renderer>();
        if (r == null) return;
        var c = r.sharedMaterial.color; c.a = a; r.sharedMaterial.color = c;
    }

    // ===================================================================== run reset
    void ResetRun()
    {
        foreach (var e in shards) if (e.tr) Destroy(e.tr.gameObject); shards.Clear();
        foreach (var f in flares) if (f.tr) Destroy(f.tr.gameObject); flares.Clear();
        foreach (var n in novas) if (n.ring) Destroy(n.ring.gameObject); novas.Clear();
        foreach (var m in motes) if (m.tr) Destroy(m.tr.gameObject); motes.Clear();

        state = State.Playing;
        runTime = 0f; score = 0; kills = 0; stage = 1;
        maxHP = 100f; hp = 100f;
        auraR = 3.3f; auraDps = 24f;
        fusion = 0f; fusionNeed = 7f;
        charge = 0f; chargeMax = 100f;
        combo = 0; comboTimer = 0f; heatMult = 1f; meltdown = false; comboFlash = 0f;
        invuln = 0f;
        spawnTimer = 0f; spawnInterval = 1.0f;
        player.position = new Vector3(0f, 0.75f, 0f);
        cam.position = new Vector3(0f, 20f, -12f);
        ApplyStarVisual();
        attract = true; attractTimer = 0f;
        SetAlpha(flashQuad, 0f); SetAlpha(tintQuad, 0f); damageFlash = 0f;
        comboText.text = ""; banner.text = "";
        hint.text = "MOVE to bathe the void in fire  ·  drag / WASD";
        hudScore.gameObject.SetActive(true); hudStat.gameObject.SetActive(true);
        RefreshHud();
    }

    void ApplyStarVisual()
    {
        float s = 1f + Mathf.Min(stage - 1, 12) * 0.09f;      // cap growth so the star never engulfs the view
        starVisual.localScale = Vector3.one * s;
        int lstage = Mathf.Min(stage, 13);
        if (starLight) { starLight.range = 14f + lstage * 1.6f; starLight.intensity = 3.0f + lstage * 0.18f; }
        auraQuad.localScale = new Vector3(auraR * 2.15f, auraR * 2.15f, 1f);
    }

    // ===================================================================== update
    void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, MAX_DT);
        fps = Mathf.Lerp(fps, 1f / Mathf.Max(0.0001f, Time.deltaTime), 0.1f);

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        ReadPointer();

        if (state == State.Playing) UpdatePlaying(dt);
        else if (state == State.Over) UpdateOver();

        UpdateNovas(dt);
        UpdateCamera(dt);
        UpdateVisualFx(dt);
        ptrWasDown = ptrDown;
    }

    // ---------------------------------------------------------------- input
    void ReadPointer()
    {
        ptrDown = false; ptrCur = Vector2.zero;
        if (Input.touchCount > 0)
        {
            var t = Input.GetTouch(0);
            ptrDown = t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled;
            ptrCur = t.position;
        }
        else if (Input.GetMouseButton(0)) { ptrDown = true; ptrCur = (Vector2)Input.mousePosition; }
        else if (Input.GetMouseButtonUp(0)) { ptrCur = (Vector2)Input.mousePosition; }
        if (ptrDown && !ptrWasDown) ptrStart = ptrCur;
    }

    Vector3 MoveInput()
    {
        float kx = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f)
                 - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f);
        float kz = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f)
                 - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f);
        Vector3 kv = new Vector3(kx, 0f, kz);
        if (kv.sqrMagnitude > 0.01f) { attract = false; return Vector3.ClampMagnitude(kv, 1f); }

        if (ptrDown)
        {
            Vector2 d = ptrCur - ptrStart;
            float ref0 = Mathf.Min(Screen.width, Screen.height) * 0.16f;
            Vector3 v = new Vector3(d.x, 0f, d.y) / Mathf.Max(1f, ref0);
            if (v.sqrMagnitude > 0.0009f) { attract = false; return Vector3.ClampMagnitude(v, 1f); }
        }
        return Vector3.zero;
    }

    // ---------------------------------------------------------------- playing
    void UpdatePlaying(float dt)
    {
        runTime += dt;
        if (invuln > 0f) invuln -= dt;

        Vector3 mv = MoveInput();
        if (attract) mv = AttractMove(dt);
        if (mv.sqrMagnitude > 0.0001f)
        {
            player.position += mv * MOVE_BASE * dt;
            lastMoveDir = mv.normalized;
            Vector3 p = player.position; p.y = 0.75f;
            Vector2 fl = new Vector2(p.x, p.z);
            if (fl.magnitude > ARENA_R - 1.4f) { fl = fl.normalized * (ARENA_R - 1.4f); p.x = fl.x; p.z = fl.y; }
            player.position = p;
        }

        // SUPERNOVA charges on a steady TIMER (a learnable ~13s rhythm), NOT per-kill — so a huge
        // melt can't spam supernovas into a permanent knockback shield. meltdown charges a bit faster.
        charge = Mathf.Min(chargeMax, charge + (meltdown ? 9.5f : 7.5f) * dt);
        if (charge >= chargeMax) Supernova();

        UpdateShards(dt);
        UpdateFlares(dt);
        UpdateMotes(dt);
        SpawnDirector(dt);

        // combo decay
        if (comboTimer > 0f) { comboTimer -= dt; if (comboTimer <= 0f) { combo = 0; } }
        heatMult = 1f + Mathf.Min(combo * 0.035f, 1.6f);
        meltdown = combo >= 22;
        if (meltdown) heatMult += 0.4f;

        RefreshHud();
        if (hp <= 0f) GameOver();
    }

    Vector3 AttractMove(float dt)
    {
        // idle demo: skim the densest cluster's edge so the star is always melting something,
        // flee if getting mobbed, never stall.
        attractTimer -= dt;
        // centroid of nearby shards
        Vector3 cen = Vector3.zero; int cnt = 0; float mobbed = 0f;
        Shard near = null; float nd = float.MaxValue;
        for (int i = 0; i < shards.Count; i++)
        {
            Vector3 d = shards[i].tr.position - player.position; d.y = 0f;
            float sq = d.sqrMagnitude;
            if (sq < 400f) { cen += shards[i].tr.position; cnt++; }
            if (sq < 16f) mobbed += 1f;
            if (sq < nd) { nd = sq; near = shards[i]; }
        }
        Vector3 dir;
        if (cnt > 0 && mobbed < 5f)
        {
            cen /= cnt;
            Vector3 to = cen - player.position; to.y = 0f;
            float dist = to.magnitude;
            // approach until aura edge sits on the cluster, then orbit
            if (dist > auraR + 1.5f) dir = to.normalized;
            else dir = new Vector3(-to.z, 0f, to.x).normalized;   // orbit tangent
        }
        else if (near != null)
        {
            Vector3 flee = player.position - near.tr.position; flee.y = 0f;
            dir = flee.normalized;
        }
        else dir = attractDir;
        if (attractTimer <= 0f) { attractTimer = Random.Range(0.7f, 1.5f); attractDir = Random.insideUnitSphere; attractDir.y = 0f; attractDir.Normalize(); }
        dir += attractDir * 0.35f;
        Vector2 fl = new Vector2(player.position.x, player.position.z);
        if (fl.magnitude > ARENA_R - 8f) dir += new Vector3(-fl.x, 0f, -fl.y).normalized * 1.6f;
        dir.y = 0f;
        return dir.sqrMagnitude > 0.001f ? Vector3.ClampMagnitude(dir, 1f) : Vector3.zero;
    }

    // ---------------------------------------------------------------- shards
    void SpawnDirector(float dt)
    {
        float mins = runTime / 60f;
        spawnInterval = Mathf.Max(0.12f, 0.80f - mins * 0.34f);
        int batch = 1 + Mathf.FloorToInt(mins * 3.2f);
        spawnTimer -= dt;
        if (spawnTimer <= 0f && shards.Count < MAX_ENEMY)
        {
            spawnTimer = spawnInterval;
            for (int i = 0; i < batch && shards.Count < MAX_ENEMY; i++) SpawnShard(mins);
        }
    }

    void SpawnShard(float mins)
    {
        int type = 0; float roll = Random.value;
        if (mins > 0.7f && roll < 0.30f) type = 1;      // runner
        if (mins > 0.85f && roll > 0.74f) type = 2;     // frost hulk (tanky anvil — the real threat)
        float hpScale = 1f + mins * 0.95f;      // steeper: late shards survive the aura long enough to reach you

        var e = new Shard { type = type };
        PrimitiveType shape; Material mat;
        if (type == 1)      { e.maxhp = 16f; e.r = 0.5f;  e.speed = 6.2f; e.dmg = 9f;  e.val = 1.6f; e.resist = 1f;    shape = PrimitiveType.Sphere; mat = shardMat1; } // runner: fast penetrator
        else if (type == 2) { e.maxhp = 120f;e.r = 1.15f; e.speed = 2.7f; e.dmg = 28f; e.val = 6f;   e.resist = 0.42f; shape = PrimitiveType.Cube;   mat = shardMat2; } // hulk: tanky anvil
        else                { e.maxhp = 18f; e.r = 0.62f; e.speed = 3.4f; e.dmg = 9f;  e.val = 1f;   e.resist = 1f;    shape = PrimitiveType.Cube;   mat = shardMat0; }
        e.maxhp *= hpScale; e.hp = e.maxhp;

        var g = GameObject.CreatePrimitive(shape);
        NoCollide(g);
        float s = e.r * 2f;
        e.baseScale = (type == 1) ? new Vector3(s, s, s) : new Vector3(s, s * 1.1f, s);
        g.transform.localScale = e.baseScale;
        g.transform.localRotation = Random.rotation;
        g.GetComponent<Renderer>().sharedMaterial = mat;

        float a = Random.value * Mathf.PI * 2f;
        Vector3 pos = player.position + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * SPAWN_RING;
        Vector2 fl = new Vector2(pos.x, pos.z);
        if (fl.magnitude > ARENA_R - 1.5f) fl = fl.normalized * (ARENA_R - 1.5f);
        pos = new Vector3(fl.x, 0.75f, fl.y);
        g.transform.position = pos;
        e.tr = g.transform;
        e.spin = Random.Range(30f, 90f) * (Random.value < 0.5f ? -1f : 1f);
        e.bob = Random.value * 6f;
        shards.Add(e);
    }

    void UpdateShards(float dt)
    {
        int n = shards.Count;
        // pairwise separation (spread the swarm)
        for (int i = 0; i < n; i++)
        {
            var a = shards[i];
            for (int j = i + 1; j < n; j++)
            {
                var b = shards[j];
                Vector3 d = a.tr.position - b.tr.position; d.y = 0f;
                float min = a.r + b.r; float sq = d.sqrMagnitude;
                if (sq < min * min && sq > 0.0001f)
                {
                    float dist = Mathf.Sqrt(sq);
                    Vector3 push = d * ((min - dist) / dist * 0.5f);
                    a.tr.position += push; b.tr.position -= push;
                }
            }
        }

        float auraR2 = auraR * auraR;
        float dmgAccum = 0f;
        for (int i = shards.Count - 1; i >= 0; i--)
        {
            var e = shards[i];
            Vector3 to = player.position - e.tr.position; to.y = 0f;
            float d = to.magnitude;
            Vector3 dir = d > 0.001f ? to / d : Vector3.forward;

            e.tr.position += dir * e.speed * dt;
            e.bob += dt * 3f;
            e.tr.Rotate(Vector3.up, e.spin * dt, Space.Self);

            // ---- HEAT AURA melt: DPS scales with proximity to the star core ----
            // NOTE: melt DPS is deliberately NOT multiplied by the combo heatMult — in a survivor
            // you kill constantly so the combo is permanently maxed; letting it scale lethality made
            // the star invincible. heatMult instead rewards combos via SCORE + supernova CHARGE.
            if (d < auraR)
            {
                float prox = 0.45f + 0.55f * (1f - d / auraR);
                float burn = auraDps * prox * e.resist * dt;
                DamageShard(e, burn);
                if (e.hp <= 0f) { KillShard(e); continue; }
            }

            // hit-flash scale punch
            if (e.flash > 0f) { e.flash -= dt; e.tr.localScale = e.baseScale * (1f + Mathf.Max(0f, e.flash) * 2f); }
            else e.tr.localScale = e.baseScale;

            // contact damage to the core
            float rr = e.r + PLAYER_R;
            if (d <= rr)
            {
                if (invuln <= 0f) dmgAccum += e.dmg * dt;
                e.tr.position -= dir * (rr - d) * 0.5f;   // shove apart
            }
        }
        if (dmgAccum > 0f && state == State.Playing)
        {
            hp -= dmgAccum;
            damageFlash = Mathf.Min(0.4f, damageFlash + dmgAccum * 0.045f);
            if (Time.frameCount % 12 == 0) Juice.Shake(0.18f);
        }
    }

    void DamageShard(Shard e, float dmg)
    {
        e.hp -= dmg;
        if (e.flash < 0.06f) e.flash = 0.06f;
    }

    void KillShard(Shard e)
    {
        Juice.Pop(e.tr.position, new Color(1f, 0.6f, 0.2f), e.type == 2 ? 16 : 9);
        Juice.Blip(e.type == 2 ? 300f : 620f, 0.06f, 0.2f);
        kills++;
        score += Mathf.RoundToInt((e.type == 2 ? 24 : 8) * heatMult);
        combo++; comboTimer = 2.2f; comboFlash = 1f;
        fusion += e.val;
        if (e.type == 2) Juice.Shake(0.28f);
        // hulks drop a PLASMA mote (fast supernova charge) — the only source, keeps supernova a
        // reward for clearing anvils rather than a constant free save.
        if (e.type == 2 && Random.value < 0.45f) SpawnMote(e.tr.position);
        if (e.tr) Destroy(e.tr.gameObject);
        shards.Remove(e);
        while (fusion >= fusionNeed) StageUp();
    }

    // ---------------------------------------------------------------- star stages
    void StageUp()
    {
        fusion -= fusionNeed;
        stage++;
        fusionNeed = 7f + stage * 4.5f;
        auraR = Mathf.Min(7f, auraR + 0.42f);       // cap so the swarm can flank from beyond the corona
        auraDps = Mathf.Min(60f, auraDps + 5f);      // cap so a growing, tankier swarm can outpace the melt
        maxHP = Mathf.Min(220f, maxHP + 9f);          // CAP tankiness; NO per-stage heal — HP only falls, so
        hp = Mathf.Min(maxHP, hp);                    // chip damage always accrues and the swarm WILL win (fail state)
        if (stage % 2 == 0 && flares.Count < 6) AddFlare();
        ApplyStarVisual();
        Juice.Score(player.position);
        Juice.Shake(0.3f);
        Juice.Blip(880f, 0.09f, 0.3f); Juice.Blip(1320f, 0.08f, 0.22f);
        banner.text = "STAR STAGE " + stage;
        bannerTimer = 1.1f;
        // a small warm shock to sell the growth
        SpawnNova(auraR * 1.6f, 0.35f, 0f);
    }

    void AddFlare()
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        NoCollide(g);
        g.transform.SetParent(flareRoot, false);
        g.transform.localScale = Vector3.one * 0.55f;
        g.GetComponent<Renderer>().sharedMaterial = flareMat;
        flares.Add(new Flare { tr = g.transform });
    }

    void UpdateFlares(float dt)
    {
        if (flares.Count == 0) return;
        flareRoot.Rotate(0f, 150f * dt, 0f);
        int n = flares.Count;
        float orbit = auraR * 0.82f;
        for (int i = 0; i < n; i++)
        {
            float a = (i / (float)n) * Mathf.PI * 2f;
            Vector3 lp = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * orbit;
            flares[i].tr.localPosition = lp;
            Vector3 wp = flares[i].tr.position;
            for (int j = shards.Count - 1; j >= 0; j--)
            {
                var e = shards[j];
                float rr = e.r + 0.75f;
                if ((e.tr.position - wp).sqrMagnitude <= rr * rr)
                {
                    float burn = 34f * e.resist * dt;
                    DamageShard(e, burn);
                    if (e.hp <= 0f) KillShard(e);
                }
            }
        }
    }

    // ---------------------------------------------------------------- supernova
    void Supernova()
    {
        charge = 0f;
        invuln = 0.6f;                    // brief mercy window, not a shield — supernova can't grant immortality
        float R = Mathf.Min(20f, 13f + stage * 1.0f);
        SpawnNova(R, 0.6f, 1f);           // damaging sweep
        Juice.Lose();                     // deep boom
        Juice.Blip(160f, 0.4f, 0.5f);
        Juice.Shake(0.7f);
        banner.text = "SUPERNOVA!";
        bannerTimer = 1.0f;
        SetAlpha(tintQuad, 0.4f);
    }

    // damage>0 : sweeping ring that hits shards as its front passes them
    void SpawnNova(float maxR, float life, float damage)
    {
        var ringGo = new GameObject("Nova");
        // thin unit ring so that scaling by radius keeps the shock band from ballooning
        ringGo.AddComponent<MeshFilter>().sharedMesh = RingMesh(0.9f, 1.0f, 72);
        var mr = ringGo.AddComponent<MeshRenderer>();
        var nsh = Shader.Find("Sprites/Default"); if (nsh == null) nsh = Shader.Find("Unlit/Transparent");
        mr.sharedMaterial = new Material(nsh) { color = new Color(1f, 0.72f, 0.32f, 1f) };
        ringGo.transform.position = new Vector3(player.position.x, 0.06f, player.position.z);
        var nv = new Nova { ring = ringGo.transform, r = 0.6f, maxR = maxR, life = life, maxLife = life };
        novas.Add(nv);
        novasDamage[nv] = damage;
    }

    readonly Dictionary<Nova, float> novasDamage = new Dictionary<Nova, float>();

    void UpdateNovas(float dt)
    {
        for (int i = novas.Count - 1; i >= 0; i--)
        {
            var nv = novas[i];
            float k = 1f - nv.life / nv.maxLife;      // 0..1 progress
            nv.r = Mathf.Lerp(0.6f, nv.maxR, Mathf.Sqrt(k));
            nv.life -= dt;
            // scale the unit ring mesh to current radius, with a thickness band
            float band = Mathf.Max(0.8f, nv.maxR * 0.12f);
            nv.ring.localScale = new Vector3(nv.r, 1f, nv.r);
            // fade
            SetAlpha(nv.ring, Mathf.Clamp01(nv.life / nv.maxLife));

            float dmg = novasDamage.TryGetValue(nv, out float dv) ? dv : 0f;
            if (dmg > 0f && state == State.Playing)
            {
                float inner = nv.r - band, outer = nv.r + band;
                for (int j = shards.Count - 1; j >= 0; j--)
                {
                    var e = shards[j];
                    if (nv.hit.Contains(e)) continue;
                    Vector3 d = e.tr.position - player.position; d.y = 0f;
                    float dist = d.magnitude;
                    if (dist >= inner && dist <= outer)
                    {
                        nv.hit.Add(e);
                        // damage + knockback — CAPPED so late-game hulks survive the blast (get shoved,
                        // not deleted) and can still accumulate into a lethal crush. Supernova clears the
                        // weak and buys space; it is not a full board-wipe.
                        DamageShard(e, Mathf.Min(150f, 55f + stage * 5f));
                        Vector3 kb = d.sqrMagnitude > 0.001f ? d.normalized : Vector3.forward;
                        e.tr.position += kb * 3.0f;
                        if (e.hp <= 0f) { KillShard(e); }
                    }
                }
            }
            if (nv.life <= 0f)
            {
                if (nv.ring) Destroy(nv.ring.gameObject);
                novasDamage.Remove(nv);
                novas.RemoveAt(i);
            }
        }
    }

    // ---------------------------------------------------------------- plasma motes
    void SpawnMote(Vector3 pos)
    {
        // cap active plasma so it never snowballs into back-to-back supernovas (immortality loop)
        if (motes.Count >= 8) { if (motes[0].tr) Destroy(motes[0].tr.gameObject); motes.RemoveAt(0); }
        var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        NoCollide(g);
        g.transform.localScale = Vector3.one * 0.5f;
        g.transform.position = new Vector3(pos.x, 0.7f, pos.z);
        var mm = MatUnlit(new Color(1f, 0.9f, 0.4f));      // glows (unlit)
        g.GetComponent<Renderer>().sharedMaterial = mm;
        motes.Add(new Mote { tr = g.transform, kind = 0, bob = Random.value * 6f, life = 9f, mat = mm });
    }

    void UpdateMotes(float dt)
    {
        float pick = auraR + 1.6f;
        for (int i = motes.Count - 1; i >= 0; i--)
        {
            var m = motes[i];
            Vector3 to = player.position - m.tr.position; to.y = 0f;
            float d = to.magnitude;
            m.bob += dt * 4f;
            m.tr.Rotate(0f, 120f * dt, 0f);
            // expire so plasma never piles up; fade out the last second
            m.life -= dt;
            if (m.life <= 0f) { Destroy(m.tr.gameObject); motes.RemoveAt(i); continue; }
            SetBase(m.mat, new Color(1f, 0.9f, 0.4f) * Mathf.Clamp01(m.life));
            // gentle constant drift toward the star + strong vacuum when near
            m.tr.position += to.normalized * 2.2f * dt;
            if (d < pick)
            {
                m.tr.position += to.normalized * Mathf.Max(7f, (pick - d) * 13f) * dt;
                if (d < 0.9f)
                {
                    charge = Mathf.Min(chargeMax, charge + 14f);
                    score += 20;
                    Juice.Blip(1050f, 0.05f, 0.18f);
                    Juice.Pop(m.tr.position, new Color(1f, 0.9f, 0.4f), 8);
                    Destroy(m.tr.gameObject); motes.RemoveAt(i); continue;
                }
            }
        }
    }

    // ---------------------------------------------------------------- game over
    void GameOver()
    {
        hp = 0f;
        state = State.Over;
        if (score > best) { best = score; PlayerPrefs.SetInt("stareater_best", best); PlayerPrefs.Save(); }
        Juice.Lose();
        hudScore.gameObject.SetActive(false); hudStat.gameObject.SetActive(false);
        hint.text = "";
        banner.text = "COLLAPSED\nSTAGE " + stage + "   ·   " + FmtTime(runTime) + "\nSCORE  " + score + "\nBEST  " + best + "\n\nTAP / R  to reignite";
        bannerTimer = 999f;
        overTimer = 3f;
    }

    float overTimer;
    void UpdateOver()
    {
        if (Input.GetKeyDown(KeyCode.R) || (ptrDown && !ptrWasDown) || Input.GetKeyDown(KeyCode.Space))
            ResetRun();
        else if (attract) { overTimer -= Time.deltaTime; if (overTimer <= 0f) ResetRun(); }  // idle demo auto-reignites
    }

    // ---------------------------------------------------------------- camera
    void UpdateCamera(float dt)
    {
        if (camComp == null) return;
        AdjustHud();
        float aspect = Mathf.Max(0.35f, camComp.aspect);
        float zoom = Mathf.Clamp(1.45f / aspect, 0.95f, 1.7f);
        Vector3 want = player.position + new Vector3(0f, 20f * zoom, -12f * zoom);
        cam.position = Vector3.SmoothDamp(cam.position, want, ref camVel, 0.12f);
        cam.rotation = Quaternion.Euler(58f, 0f, 0f);
    }

    // ---------------------------------------------------------------- fx + hud text
    float bannerTimer;
    void UpdateVisualFx(float dt)
    {
        damageFlash = Mathf.Max(0f, damageFlash - dt * 1.6f);
        SetAlpha(flashQuad, Mathf.Clamp01(damageFlash));

        // warm tint fades (supernova / meltdown). keep a faint glow during meltdown so the cold
        // shards stay readable (a heavy tint washed out the warm-vs-cold contrast).
        var tr = tintQuad.GetComponent<Renderer>();
        float curA = tr.sharedMaterial.color.a;
        float targetA = (meltdown && state == State.Playing) ? 0.07f : 0f;
        SetAlpha(tintQuad, Mathf.MoveTowards(curA, targetA, dt * 1.4f));

        // aura pulse
        auraPulse = 0.42f + Mathf.Sin(Time.time * 4f) * 0.06f + (meltdown ? 0.12f : 0f);
        if (auraQuad) SetAlpha(auraQuad, auraPulse);

        // star shell slow spin + living pulse on the emission
        if (starVisual) starVisual.localRotation = Quaternion.Euler(0f, Time.time * 20f, 0f);
        float pulse = 0.9f + Mathf.Sin(Time.time * 5f) * 0.1f + (meltdown ? 0.1f : 0f);
        SetBase(starCoreMat, new Color(1f, 0.95f, 0.82f) * Mathf.Clamp01(pulse + 0.06f));
        SetBase(starShellMat, new Color(1f, 0.5f, 0.13f) * Mathf.Clamp01(pulse));

        if (comboFlash > 0f) comboFlash = Mathf.Max(0f, comboFlash - dt * 2.5f);
        if (combo >= 4 && state == State.Playing)
        {
            comboText.text = (meltdown ? "MELTDOWN  x" : "HEAT  x") + combo;
            comboText.color = meltdown ? new Color(1f, 0.45f, 0.25f) : new Color(1f, 0.72f, 0.32f);
            comboText.characterSize = (0.10f + comboFlash * 0.04f) * hudScale;
        }
        else comboText.text = "";

        if (bannerTimer > 0f && state == State.Playing)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) banner.text = "";
        }
        if (runTime > 6f && state == State.Playing) hint.text = "";

        if (showDbg && dbg)
            dbg.text = string.Format("fps {0:00}  state {1}  shards {2}  flares {3}  novas {4}  motes {5}\nstage {6} aura {7:0.0} dps {8:0} fus {9:0}/{10:0}\ncharge {11:0}/{12:0} hp {13:0}/{14:0} combo {15} heat x{16:0.00} inv {17:0.0}\nattract {18} spawnInt {19:0.00}",
                fps, state, shards.Count, flares.Count, novas.Count, motes.Count,
                stage, auraR, auraDps, fusion, fusionNeed,
                charge, chargeMax, hp, maxHP, combo, heatMult, invuln,
                attract, spawnInterval);

        RefreshBars();
    }

    void RefreshHud()
    {
        if (hudScore) hudScore.text = "SCORE  " + score + "\nSTAGE " + stage;
        if (hudStat) hudStat.text = FmtTime(runTime) + "\nKILLS " + kills + "\nBEST " + best;
    }

    static string FmtTime(float t)
    {
        int m = (int)(t / 60f), s = (int)(t % 60f);
        return string.Format("{0}:{1:00}", m, s);
    }
}
