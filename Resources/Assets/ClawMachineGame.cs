using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// ─────────────────────────────────────────────────────────────────────────────
//  UFO 캐쳐 스타일 3D 뽑기 시뮬레이터 (완만해진 경사 + 타이트한 틈새 패치 + WebGL NPOT 픽스)
// ─────────────────────────────────────────────────────────────────────────────
public class ClawMachineGame : MonoBehaviour
{
    class Prize
    {
        public GameObject go;
        public Rigidbody rb;
        public Vector3 half;
        public bool collected;
    }

    enum State { Menu, Aim, Drop }
    enum Phase { Descend, Close, Ascend, Move, Open, Done }

    const float W = 0.85f;
    const float D = 0.5f;   // 💡 앞뒤(깊이) 절반. (큰 경품 박스를 뒤에 같은 크기로 두려고 약간 늘림)
    const float H = 2.2f;
    const float TopY = 1.8f;
    const float PlayX = 0.68f;   // 좌우 이동 한계(벽 안쪽)
    const float PlayZ = 0.38f;   // 앞쪽 이동 한계
    const float ClawZBack = -0.06f; // 💡 뒤쪽 이동 한계 — 살짝 더 뒤까지(진열 더미 z≈-0.1 직전)
    const float PivotX = 0.08f;
    const float CollectY = -0.2f;
    const float TimeLimit = 12f; // 한 코인당 제한 시간(초)
    const float PlayZc = 0.2f;   // 💡 플레이 영역(봉·박스·배출구) 중심 깊이
    const float HomeZ = 0.33f;   // 💡 집게 시작 깊이(조금 더 앞쪽)
    static readonly Vector3 HomePos = new Vector3(-PlayX, TopY, HomeZ); // 좌측-앞 모서리에서 시작

    [Header("🪟 유리 재질 (WebGL 투명 버그 해결용)")]
    public Material customGlassMat;

    [Header("집게 물리 (얇은 집게, 1N 긁기 전용)")]
    [Range(0f, 50f)] public float gripForce = 1.19f;    
    [Range(60f, 700f)] public float motorSpeed = 235f;
    [Range(15f, 55f)] public float openAngle = 25f;
    [Range(-45f, 10f)] public float closeAngle = -21.04f;
    [Range(0.01f, 2.5f)] public float padFriction = 1.2f; 
    [Range(0.3f, 1.2f)] public float dropSpeed = 0.6f;

    [Header("박스(경품) 크기 조절 (19x15x10 표준 규격)")]
    [Range(0.1f, 0.4f)] public float boxWidthX = 0.38f;   
    [Range(0.05f, 0.3f)] public float boxHeightY = 0.2000f; 
    [Range(0.1f, 0.4f)] public float boxDepthZ = 0.2691f;   

    [Header("난이도 세팅 (경사형 & 타이트한 틈새)")]
    [Range(0.08f, 0.4f)] public float railGap = 0.293f;
    [Range(0f, 0.1f)] public float railSlope = 0.030f; 
    [Range(0.2f, 40.0f)] public float boxMass = 1.28f;
    [Range(0f, 0.08f)] public float boxComOffset = 0.039f;

    [Header("물리 마찰(고급)")]
    [Range(0.05f, 1f)] public float railFriction = 0.128f;
    [Range(0.1f, 1.2f)] public float prizeFriction = 0.435f;

    State state = State.Menu;
    Phase phase;
    string modeName = "-";
    float descendY = 0.30f;
    int coins = 0;
    int collected = 0;
    string msg = "모드를 선택하세요";
    float phaseT = 0f;

    int coinsUsed = 0;     
    int successCount = 0;  
    int failCount = 0;     
    float timeLeft = TimeLimit;
    bool wonThisDrop = false;
    float successFlash = 0f; 

    bool bridgeMode = false;
    bool pendingRespawn = false;
    Vector3 bridgeBoxPos, bridgeBoxHalf;
    Rigidbody bridgeRb;   // 막 스폰된 다리 박스(스폰 직후 잠깐 고정용)
    float settleT = 0f;   // 스폰 직후 kinematic 유지 시간
    Transform railsRoot;  // 💡 리셋 시 이전 봉 세트를 지우기 위한 참조(중복 생성 방지)

    Rigidbody trolleyRb;
    Transform clawHead;
    Transform pole;
    
    struct Prong 
    { 
        public Rigidbody rb; 
        public HingeJoint hj; 
        public float side; 
    }
    
    readonly List<Prong> prongs = new List<Prong>();
    Vector3 clawPos = new Vector3(0, TopY, 0);
    bool gripClosed = true; 

    readonly List<Prize> prizes = new List<Prize>();

    Camera cam;
    float camYaw = 180f; 
    float camPitch = 30f;
    float camDist = 3.00f;
    readonly Vector3 camTarget = new Vector3(0, 0.80f, 0.15f);

    Material mWhite, mPink, mSilver, mGlass, mFloor, mRail, mRedTip, mDark;
    PhysicsMaterial pmPrize, pmRail, pmFloor, pmClaw, pmSlip;
    static Shader litShader;

    void InitShaders() 
    { 
        litShader = Shader.Find("Standard"); 
    }

    Material Std(Color c, float metallic, float smooth)
    {
        var m = new Material(litShader != null ? litShader : Shader.Find("Sprites/Default"));
        m.color = c; 
        m.SetFloat("_Metallic", metallic); 
        m.SetFloat("_Glossiness", smooth); 
        return m;
    }

    Material Emissive(Color c, Color emission, float metallic = 0.3f, float smooth = 0.5f)
    {
        var m = Std(c, metallic, smooth);
        m.EnableKeyword("_EMISSION"); 
        m.SetColor("_EmissionColor", emission); 
        return m;
    }

    Material Glass(Color c, float alpha)
    {
        var m = Std(c, 0f, 0.95f);
        m.SetFloat("_Mode", 3); 
        m.SetColor("_Color", new Color(c.r, c.g, c.b, alpha)); 
        m.color = new Color(c.r, c.g, c.b, alpha);
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0); 
        m.DisableKeyword("_ALPHATEST_ON"); 
        m.EnableKeyword("_ALPHABLEND_ON");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON"); 
        m.renderQueue = 3000; 
        return m;
    }

    static GameObject Cube(string name, Vector3 pos, Vector3 size, Material mat, bool collider, Transform parent = null)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube); 
        go.name = name;
        if (!collider) Destroy(go.GetComponent<Collider>());
        go.transform.localScale = size; 
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = pos; 
        go.GetComponent<MeshRenderer>().sharedMaterial = mat; 
        return go;
    }

    static GameObject Cyl(string name, Vector3 pos, float radius, float length, Material mat, bool collider, Transform parent = null)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder); 
        go.name = name;
        if (!collider) Destroy(go.GetComponent<Collider>());
        go.transform.localScale = new Vector3(radius * 2f, length * 0.5f, radius * 2f);
        if (parent != null) go.transform.SetParent(parent, false); 
        go.transform.position = pos;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat; 
        return go;
    }

    static GameObject Sphere(string name, Vector3 localPos, float radius, Material mat, Transform parent)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); 
        go.name = name; 
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false); 
        go.transform.localPosition = localPos;
        go.transform.localScale = Vector3.one * (radius * 2f); 
        go.GetComponent<MeshRenderer>().sharedMaterial = mat; 
        return go;
    }

    void Awake()
    {
        Application.targetFrameRate = 60;
        Physics.defaultSolverIterations = 16;
        Physics.defaultSolverVelocityIterations = 4;
        // 💡 WebGL은 첫 프레임 로딩 지연으로 deltaTime이 크게 튀어, 물리가 "캐치업"으로
        //    여러 스텝을 몰아 계산하며 얇은 봉을 순간 관통하는 경우가 있음 → 캐치업 폭 제한
        Time.maximumDeltaTime = 0.05f;
        KeepStrippedTypes(); // WebGL 코드 스트리핑으로 콜라이더 클래스가 통째로 제거되는 것 방지

        InitShaders(); 
        BuildMaterials(); 
        BuildPhysicMaterials(); 
        BuildCamera(); 
        BuildLights();
        BuildCabinet(); 
        BuildCommonFloor();
        BuildDecor();
        BuildClaw();
        BuildAudio();
        StartMode();
    }

    // 💡 WebGL(IL2CPP) 빌드는 "코드에서 타입 이름을 직접 쓴 적 없는 클래스"를 링커가 통째로
    //    제거합니다. GameObject.CreatePrimitive(Cylinder/Sphere/Capsule)는 내부적으로
    //    MeshCollider/SphereCollider/CapsuleCollider를 자동으로 붙이는데, 우리 코드는 항상
    //    부모 클래스 Collider로만 다뤄서 이 구체 타입들을 직접 참조한 적이 없어 스트리핑됨
    //    → 봉(Cylinder)의 콜라이더가 빌드에서 통째로 사라져 박스가 그냥 통과하는 원인이었음.
    //    여기서 한 번 직접 Add/Remove해서 링커가 "실제로 쓰는 타입"으로 인식하게 강제한다.
    void KeepStrippedTypes()
    {
        var keep = new GameObject("__KeepTypes");
        keep.AddComponent<MeshCollider>();
        keep.AddComponent<SphereCollider>();
        keep.AddComponent<CapsuleCollider>();
        Destroy(keep);
    }

    AudioSource bgmSrc, motorSrc, sfxSrc;
    AudioClip clipMotor, clipBgm, clipCoin, clipClick, clipClunk, clipSuccess, clipFail;
    bool clawMoved, motorOn;
    bool muted = false;
    float coinFlash = 0f; 

    // 💡 [추가] 모바일 UI 버튼의 눌림 상태를 저장할 변수들
    bool uiLeft, uiRight, uiUp, uiDown;

    AudioClip Synth(string name, float seconds, System.Func<float, float> wave, int sr = 44100)
    {
        int n = (int)(seconds * sr);
        var d = new float[n];
        for (int i = 0; i < n; i++) d[i] = Mathf.Clamp(wave(i / (float)sr), -1f, 1f);
        var c = AudioClip.Create(name, n, 1, sr, false);
        c.SetData(d, 0);
        return c;
    }

    AudioClip MakeArp(string name, float[] notes, float step, float noteLen, float dur, float amp, int sr = 44100)
    {
        int n = (int)(dur * sr);
        var d = new float[n];
        for (int k = 0; k < notes.Length; k++)
        {
            int s = (int)(k * step * sr), len = (int)(noteLen * sr);
            for (int j = 0; j < len && s + j < n; j++)
            {
                float t = j / (float)sr, env = Mathf.Exp(-5f * t);
                d[s + j] += amp * env * Mathf.Sin(2f * Mathf.PI * notes[k] * t);
            }
        }
        var c = AudioClip.Create(name, n, 1, sr, false);
        c.SetData(d, 0);
        return c;
    }

    void BuildAudio()
    {
        if (cam != null && cam.GetComponent<AudioListener>() == null) cam.gameObject.AddComponent<AudioListener>();
        var go = new GameObject("Audio");
        bgmSrc = go.AddComponent<AudioSource>();
        motorSrc = go.AddComponent<AudioSource>();
        sfxSrc = go.AddComponent<AudioSource>();

        float PI = Mathf.PI;
        clipMotor = Synth("motor", 1f, t =>
        {
            float hum = 0.50f * Mathf.Sin(2f * PI * 120f * t)
                      + 0.42f * Mathf.Sin(2f * PI * 121f * t)   
                      + 0.16f * Mathf.Sin(2f * PI * 240f * t)   
                      + 0.10f * Mathf.Sin(2f * PI * 60f * t);   
            float whine = 0.045f * Mathf.Sin(2f * PI * 480f * t); 
            float trem = 0.90f + 0.10f * Mathf.Sin(2f * PI * 7f * t);
            return (hum + whine) * 0.42f * trem;
        });
        clipBgm = Resources.Load<AudioClip>("bgm");
        if (clipBgm == null) clipBgm = MakeBgm();
        clipCoin = Synth("coin", 0.24f, t =>
        {
            float f = (t < 0.08f) ? 784f : 1047f, lt = (t < 0.08f) ? t : t - 0.08f;
            return 0.22f * Mathf.Exp(-9f * lt) * Mathf.Sin(2f * PI * f * t);
        });
        clipClick = Synth("click", 0.05f, t => 0.35f * Mathf.Exp(-60f * t) * Mathf.Sign(Mathf.Sin(2f * PI * 1500f * t)));
        clipClunk = Synth("clunk", 0.18f, t => 0.4f * Mathf.Exp(-22f * t) * Mathf.Sin(2f * PI * 90f * t));
        clipSuccess = MakeArp("success", new[] { 523.25f, 659.25f, 783.99f, 1046.5f }, 0.13f, 0.28f, 0.72f, 0.3f);
        clipFail = MakeFail(); 

        bgmSrc.clip = clipBgm; bgmSrc.loop = true; bgmSrc.volume = 0.30f; 
        motorSrc.clip = clipMotor; motorSrc.loop = true; motorSrc.volume = 0.05f; motorSrc.Play();
        sfxSrc.playOnAwake = false;
    }

    AudioClip MakeFail(int sr = 44100)
    {
        float[] notes = { 261.63f, 220.00f }; 
        float noteDur = 0.13f;
        int n = (int)(notes.Length * noteDur * sr);
        var d = new float[n];
        int pos = 0;
        for (int k = 0; k < notes.Length; k++)
        {
            bool last = (k == notes.Length - 1);
            int len = (int)(noteDur * sr);
            for (int j = 0; j < len && pos + j < n; j++)
            {
                float t = j / (float)sr;
                float vib = 1f + 0.05f * Mathf.Sin(2f * Mathf.PI * 6f * t);
                float f = notes[k] * vib;
                float ph = t * f;
                float saw = 2f * (ph - Mathf.Floor(ph + 0.5f));
                float tone = 0.5f * Mathf.Sin(2f * Mathf.PI * f * t) + 0.5f * saw;
                float atk = Mathf.Min(1f, j / (0.015f * sr));
                float env = atk * Mathf.Exp(-(last ? 3.5f : 4.5f) * t);
                d[pos + j] += 0.26f * env * tone;
            }
            pos += len;
        }
        var c = AudioClip.Create("fail", n, 1, sr, false);
        c.SetData(d, 0);
        return c;
    }

    AudioClip MakeBgm(int sr = 44100)
    {
        float chordDur = 0.8f, eighth = 0.2f;
        float[][] ch = {
            new[]{220.00f, 261.63f, 329.63f, 110.00f}, 
            new[]{174.61f, 220.00f, 261.63f, 87.31f},  
            new[]{196.00f, 246.94f, 293.66f, 98.00f},  
            new[]{164.81f, 207.65f, 246.94f, 82.41f}   
        };
        int n = (int)(chordDur * ch.Length * sr);
        var d = new float[n];

        float Saw(float x) { float p = x / (2f * Mathf.PI); p -= Mathf.Floor(p); return 2f * p - 1f; }
        void Add(int start, float dur, float f, float amp, float dec, System.Func<float, float> osc)
        {
            int len = (int)(dur * sr);
            for (int j = 0; j < len && start + j < n; j++)
            {
                float t = j / (float)sr;
                d[start + j] += amp * Mathf.Exp(-dec * t) * osc(2f * Mathf.PI * f * t);
            }
        }

        for (int ci = 0; ci < ch.Length; ci++)
        {
            int b = (int)(ci * chordDur * sr);
            var c = ch[ci];
            for (int e = 0; e < 4; e++) Add(b + (int)(e * eighth * sr), 0.16f, c[3], 0.16f, 9f, Saw);   
            for (int s = 0; s < 8; s++) Add(b + (int)(s * 0.1f * sr), 0.09f, c[s % 3] * 2f, 0.07f, 10f, Mathf.Sin); 
            for (int e = 0; e < 4; e += 2) Add(b + (int)(e * eighth * sr), 0.12f, 70f, 0.22f, 18f, Mathf.Sin);      
            for (int e = 0; e < 4; e++) Add(b + (int)((e + 0.5f) * eighth * sr), 0.03f, 5000f, 0.05f, 80f, Mathf.Sin); 
        }
        var clip = AudioClip.Create("bgm", n, 1, sr, false);
        clip.SetData(d, 0);
        return clip;
    }

    void BuildDecor()
    {
        var root = new GameObject("Decor").transform;

        int cols = 5;
        int rows = 5;             
        float boxW = boxDepthZ;   
        float boxH = boxWidthX;   
        float boxD = boxHeightY;  
        float gap = 0.03f;

        float startX = -(cols - 1) * (boxW + gap) * 0.5f;
        float startY = boxH * 0.5f;                       
        float backZ = PlayZc - 0.48f;   // 💡 봉(뒤쪽 z≈-0.1)과 겹치지 않게 진열 벽을 뒤로 이동

        float Hsh(int seed)
        {
            float s = Mathf.Sin(seed * 12.9898f) * 43758.5453f;
            return s - Mathf.Floor(s);
        }

        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int i = r * cols + c;
                float jx = (Hsh(i * 2 + 1) - 0.5f) * 0.005f;
                float jy = (Hsh(i * 7 + 3) - 0.5f) * 0.004f;
                float jz = (Hsh(i * 5 + 2) - 0.5f) * 0.010f;
                var sz = new Vector3(boxW, boxH, boxD);
                float tilt = (Hsh(i * 11 + 5) - 0.5f) * 1.6f;
                float yaw = (Hsh(i * 3 + 9) - 0.5f) * 2.8f;
                float roll = (Hsh(i * 13 + 4) - 0.5f) * 1.2f;
                var wp = new Vector3(startX + c * (boxW + gap) + jx, startY + r * (boxH + gap) + jy, backZ + jz);
                
                // 💡 [수정] 박스 비주얼 생성 후, 카드 붙이는 로직 추가
                var bx = FigureBoxVisual(root, wp, Quaternion.Euler(tilt, yaw, roll), sz, 2, FigMat(i));
                
                // 💡 [핵심] 아까 수정했던 AddFrontCard를 호출하여 앞/뒤면 텍스처를 붙임
                // 'frontAxis = 2'는 앞면 축을 의미합니다
                AddFrontCard(bx, sz, 2, FigMat(i,false)); 
            }
    }

    void BuildPhysicMaterials()
    {
        pmRail = new PhysicsMaterial("rail");
        pmRail.dynamicFriction = railFriction; 
        pmRail.staticFriction = railFriction + 0.05f; 
        pmRail.frictionCombine = PhysicsMaterialCombine.Minimum;
        
        pmPrize = new PhysicsMaterial("prize");
        pmPrize.dynamicFriction = prizeFriction; 
        pmPrize.staticFriction = prizeFriction + 0.1f; 
        pmPrize.frictionCombine = PhysicsMaterialCombine.Average;
        
        pmFloor = new PhysicsMaterial("floor");
        pmFloor.dynamicFriction = 0.6f; 
        pmFloor.staticFriction = 0.7f;
        
        pmClaw = new PhysicsMaterial("claw");
        pmClaw.dynamicFriction = Mathf.Min(padFriction, 0.4f);          // 💡 들어올림 방지: 패드 마찰 상한
        pmClaw.staticFriction = Mathf.Min(padFriction, 0.4f) + 0.05f;
        pmClaw.frictionCombine = PhysicsMaterialCombine.Minimum;

        pmSlip = new PhysicsMaterial("slip");
        pmSlip.dynamicFriction = 0f;
        pmSlip.staticFriction = 0f;
        pmSlip.bounciness = 0f;
        pmSlip.frictionCombine = PhysicsMaterialCombine.Minimum;
    }

    static void SetPM(GameObject go, PhysicsMaterial pm) 
    { 
        var c = go.GetComponent<Collider>(); 
        if (c != null) 
            c.sharedMaterial = pm; 
    }

    void BuildMaterials()
    {
        mWhite = Std(new Color(0.93f, 0.93f, 0.95f), 0f, 0.15f);
        mPink = Emissive(new Color(1f, 0.37f, 0.64f), new Color(0.23f, 0.05f, 0.13f), 0.0f, 0.3f);
        mSilver = Std(new Color(0.74f, 0.76f, 0.80f), 0.8f, 0.45f);
        
        // 💡 유리 재질 자동 로드: Inspector에 없으면 Resources/RealGlass를 사용, 그것도 없으면 코드 생성
        if (customGlassMat == null) customGlassMat = Resources.Load<Material>("RealGlass");
        if (customGlassMat != null) mGlass = customGlassMat;
        else mGlass = Glass(new Color(0.85f, 0.92f, 1f), 0.055f); // 옅게 → 반투명 정렬 아티팩트 눈에 안 띄게

        mFloor = Std(new Color(0.16f, 0.2f, 0.31f), 0.05f, 0.12f);
        mRail = Std(new Color(0.82f, 0.84f, 0.88f), 0.8f, 0.5f);
        mRedTip = Emissive(new Color(0.89f, 0.23f, 0.23f), new Color(0.2f, 0.02f, 0.02f), 0.3f, 0.6f);
        mDark = Std(new Color(0.08f, 0.08f, 0.11f), 0.5f, 0.7f);
    }

    Material[] _figMats;
    // 💡 [수정] 박스 측면/뒷면용 뒤집힌 마테리얼 생성 추가
    Material FigMat(int variant, bool flipX = false)
    {
        if (_figMats == null) _figMats = new Material[16]; // 배열 크기 8 -> 16으로 확장
        int index = ((variant % 8) + (flipX ? 8 : 0));
        if (_figMats[index] == null)
        {
            var m = new Material(litShader != null ? litShader : Shader.Find("Sprites/Default"));
            var tex = MakeFigureTex(variant);
            m.mainTexture = tex;
            if (flipX) {
                m.mainTextureScale = new Vector2(-1, 1);
                m.mainTextureOffset = new Vector2(1, 0);
            }
            m.color = Color.white;
            _figMats[index] = m;
        }
        return _figMats[index];
    }

    Material _sideMat;
    Material SideMat()
    {
        if (_sideMat != null) return _sideMat;
        
        // 💡 [WebGL NPOT 패치] 64x96 -> 64x128 (2의 제곱수 해상도로 교정)
        int w = 64, h = 128;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false); 
        var p = new Color32[w * h];
        void F(int x0, int y0, int x1, int y1, Color c)
        {
            x0 = Mathf.Clamp(x0, 0, w); x1 = Mathf.Clamp(x1, 0, w); y0 = Mathf.Clamp(y0, 0, h); y1 = Mathf.Clamp(y1, 0, h);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) p[y * w + x] = c;
        }
        void D(int X, int Y, int r, Color c) { for (int y = -r; y <= r; y++) for (int x = -r; x <= r; x++) if (x * x + y * y <= r * r && X + x >= 0 && X + x < w && Y + y >= 0 && Y + y < h) p[(Y + y) * w + (X + x)] = c; }
        void E(int X, int Y, int rx, int ry, Color c)
        {
            for (int y = -ry; y <= ry; y++) for (int x = -rx; x <= rx; x++)
            { float fx = x / (float)rx, fy = y / (float)ry; if (fx * fx + fy * fy <= 1f && X + x >= 0 && X + x < w && Y + y >= 0 && Y + y < h) p[(Y + y) * w + (X + x)] = c; }
        }
        Color bg = Color.HSVToRGB(0.52f, 0.10f, 0.80f);   
        Color teal = Color.HSVToRGB(0.49f, 0.50f, 0.78f);
        Color gray = new Color(0.50f, 0.50f, 0.56f);
        Color hair = Color.HSVToRGB(0.49f, 0.42f, 0.86f);
        Color hairSh = Color.HSVToRGB(0.50f, 0.55f, 0.62f);
        Color skin = new Color(1f, 0.89f, 0.83f);
        Color top = Color.HSVToRGB(0.58f, 0.06f, 0.80f);  
        Color skirt = Color.HSVToRGB(0.55f, 0.18f, 0.42f);
        Color eye = Color.HSVToRGB(0.49f, 0.55f, 0.52f);
        int cx = 33;
        for (int i = 0; i < p.Length; i++) p[i] = bg;

        F(0, 0, 4, h, teal);
        F(9, h - 13, w - 7, h - 5, teal);
        for (int k = 0; k < 3; k++) F(13 + k * 11, h - 11, 13 + k * 11 + 6, h - 7, new Color(1, 1, 1, 0.9f));

        E(21, 50, 5, 22, hairSh); E(45, 50, 5, 22, hairSh);   
        E(21, 52, 3, 18, hair); E(45, 52, 3, 18, hair);       
        D(cx, 63, 11, hairSh);                                
        for (int y = 34; y < 52; y++) { int hw = (int)Mathf.Lerp(11f, 6f, (y - 34) / 18f); F(cx - hw, y, cx + hw, y + 1, skirt); } 
        F(cx - 6, 52, cx + 6, 60, top);                       
        for (int y = 52; y < 59; y++) F(cx - 1, y, cx + 2, y + 1, teal); 
        F(cx - 2, 58, cx + 2, 62, skin);                      
        D(cx, 64, 9, skin);                                   
        for (int y = -9; y <= 9; y++) for (int x = -9; x <= 9; x++) if (x * x + y * y <= 81 && (64 + y) >= 67) { int xx = cx + x, yy = 64 + y; if (xx >= 0 && xx < w && yy >= 0 && yy < h) p[yy * w + xx] = hair; } 
        E(cx - 8, 62, 2, 7, hair); E(cx + 8, 62, 2, 7, hair); 
        F(cx - 5, 62, cx - 2, 65, eye); F(cx + 2, 62, cx + 5, 65, eye); 

        for (int k = 0; k < 3; k++) F(9, 22 - k * 5, w - 9, 25 - k * 5, k == 0 ? gray : new Color(0.62f, 0.62f, 0.68f));
        for (int x = 9; x < w - 7; x += 2) F(x, 6, x + 1, 13, Color.black);

        F(0, 0, w, 2, teal); F(0, h - 2, w, h, teal); F(w - 2, 0, w, h, teal);
        tex.SetPixels32(p); tex.Apply(); tex.wrapMode = TextureWrapMode.Clamp;
        var m = new Material(litShader != null ? litShader : Shader.Find("Sprites/Default"));
        m.mainTexture = tex; m.color = Color.white; m.SetFloat("_Metallic", 0f); m.SetFloat("_Glossiness", 0.1f);
        _sideMat = m; return m;
    }

    void AddFrontCard(Transform root, Vector3 size, int frontAxis, Material fig)
    {
        Material mat = new Material(fig);
        
        void MakeFace(bool isBack, bool shouldFlip, Vector3 customUp)
        {
            var card = GameObject.CreatePrimitive(PrimitiveType.Cube);
            card.name = isBack ? "ArtBack" : "ArtFront"; 
            Destroy(card.GetComponent<Collider>());
            card.transform.SetParent(root, false);
            
            Material faceMat = new Material(mat);
            // 💡 이미지 반전 처리
            if (shouldFlip) {
                faceMat.mainTextureScale = new Vector2(-1, 1);
                faceMat.mainTextureOffset = new Vector2(1, 0);
            }

            Vector3 outward; float fw, fh, off;
            switch (frontAxis)
            {
                case 1:  
                    outward = isBack ? Vector3.down : Vector3.up;      
                    fw = size.z; fh = size.x; off = size.y * 0.5f; 
                    break; 
                case 3:  
                    outward = isBack ? Vector3.right : Vector3.left;    
                    fw = size.z; fh = size.y; off = size.x * 0.5f; 
                    break; 
                default: 
                    outward = isBack ? Vector3.back : Vector3.forward; 
                    fw = size.x; fh = size.y; off = size.z * 0.5f; 
                    break; 
            }
            
            card.transform.localRotation = Quaternion.LookRotation(outward, customUp);
            card.transform.localScale = new Vector3(fw, fh, 0.003f);
            card.transform.localPosition = outward * (off + 0.002f);
            
            var mr = card.GetComponent<MeshRenderer>();
            mr.sharedMaterial = faceMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // 💡 [수정] 박스별 축에 따라 upRef(customUp)를 분기하여 이미지가 뒤집히지 않게 고정
        if (frontAxis == 1) {
            MakeFace(false, true, Vector3.left);
            MakeFace(true, true, Vector3.left);
        } else {
            MakeFace(false, false, Vector3.up);
            MakeFace(true, true, Vector3.up);
        }
    }

    Transform FigureBoxVisual(Transform parent, Vector3 worldPos, Quaternion rot, Vector3 size, int frontAxis, Material fig)
    {
        var bx = new GameObject("FigureBox").transform;
        bx.SetParent(parent, false);
        bx.position = worldPos; bx.rotation = rot;
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(bx, false);
        body.transform.localScale = size;
        var bcol = body.GetComponent<Collider>();   
        if (bcol != null) bcol.sharedMaterial = pmPrize;
        var bmr = body.GetComponent<MeshRenderer>();
        bmr.sharedMaterial = SideMat();
        bmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        bmr.receiveShadows = true;
        AddFrontCard(bx, size, frontAxis, fig);
        return bx;
    }

    Texture2D MakeFigureTex(int variant)
    {
        // 💡 [WebGL NPOT 패치] 128x192 -> 128x256 (2의 제곱수 해상도로 교정)
        int w = 128, h = 256;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false); 
        var px = new Color32[w * h];
        var rnd = new System.Random(variant * 7919 + 31);
        int cx = w / 2;

        float dv = (variant % 5) * 0.02f;                         
        Color bgTop = Color.HSVToRGB(0.52f, 0.04f, 1.00f);
        Color bgBot = Color.HSVToRGB(0.52f, 0.12f + dv, 0.98f);
        Color accent = Color.HSVToRGB(0.50f, 0.35f, 0.90f);
        Color hair = Color.HSVToRGB(0.49f, 0.40f, 0.88f);        
        Color hairSh = Color.HSVToRGB(0.50f, 0.52f, 0.66f);
        Color hairHi = Color.HSVToRGB(0.47f, 0.16f, 1.00f);
        Color skin = new Color(1.00f, 0.89f, 0.83f);
        Color top = Color.HSVToRGB(0.58f, 0.05f, 0.82f);         
        Color topSh = Color.HSVToRGB(0.58f, 0.10f, 0.62f);
        Color skirt = Color.HSVToRGB(0.55f, 0.18f, 0.40f);       
        Color skirtHi = Color.HSVToRGB(0.55f, 0.14f, 0.55f);
        Color tie = Color.HSVToRGB(0.49f, 0.55f, 0.80f);         
        Color boot = Color.HSVToRGB(0.60f, 0.10f, 0.26f);        
        Color eye = Color.HSVToRGB(0.49f, 0.55f, 0.72f);         
        Color eyeHi = new Color(0.85f, 0.98f, 1.00f);
        Color line = new Color(0.30f, 0.28f, 0.34f);
        Color blush = new Color(1.00f, 0.78f, 0.78f);
        Color teal = Color.HSVToRGB(0.49f, 0.55f, 0.78f);
        Color badge = new Color(1.00f, 0.62f, 0.74f);

        void Px(int X, int Y, Color c) { if (X >= 0 && X < w && Y >= 0 && Y < h) px[Y * w + X] = c; }
        void Fill(int x0, int y0, int x1, int y1, Color col)
        {
            x0 = Mathf.Clamp(x0, 0, w); x1 = Mathf.Clamp(x1, 0, w);
            y0 = Mathf.Clamp(y0, 0, h); y1 = Mathf.Clamp(y1, 0, h);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) px[y * w + x] = col;
        }
        void Disc(int X, int Y, int r, Color c) { for (int y = -r; y <= r; y++) for (int x = -r; x <= r; x++) if (x * x + y * y <= r * r) Px(X + x, Y + y, c); }
        void Ell(int X, int Y, int rx, int ry, Color c)
        {
            for (int y = -ry; y <= ry; y++) for (int x = -rx; x <= rx; x++)
            { float fx = x / (float)rx, fy = y / (float)ry; if (fx * fx + fy * fy <= 1f) Px(X + x, Y + y, c); }
        }

        for (int y = 0; y < h; y++)
        {
            Color row = Color.Lerp(bgBot, bgTop, y / (float)h);
            for (int x = 0; x < w; x++) px[y * w + x] = row;
        }
        Color deco = Color.HSVToRGB(0.50f, 0.10f, 1f);
        Disc(98, 150, 16, deco); Disc(26, 70, 12, deco); Disc(104, 56, 9, deco);

        Ell(30, 92, 13, 58, hairSh); Ell(98, 92, 13, 58, hairSh);   
        Ell(31, 95, 10, 54, hair); Ell(97, 95, 10, 54, hair);       
        Ell(28, 110, 3, 30, hairHi); Ell(94, 110, 3, 30, hairHi);   
        Disc(37, 151, 4, tie); Disc(91, 151, 4, tie);               
        Disc(cx, 145, 23, hairSh); Disc(cx, 149, 21, hair);         

        Fill(cx - 9, 42, cx - 3, 76, boot); Fill(cx + 3, 42, cx + 9, 76, boot);
        Fill(cx - 9, 72, cx - 3, 74, tie); Fill(cx + 3, 72, cx + 9, 74, tie);   
        Fill(cx - 10, 42, cx - 2, 45, line); Fill(cx + 2, 42, cx + 10, 45, line); 
        Fill(cx - 8, 76, cx - 3, 90, skin); Fill(cx + 3, 76, cx + 8, 90, skin);   

        for (int y = 88; y < 106; y++)
        {
            float t = (y - 88) / 18f;
            int hw = (int)Mathf.Lerp(27f, 12f, t);
            Fill(cx - hw, y, cx + hw, y + 1, skirt);
            Px(cx - hw / 2, y, skirtHi); Px(cx, y, skirtHi); Px(cx + hw / 2, y, skirtHi);
        }
        Fill(cx - 27, 86, cx + 27, 88, skirtHi);                   

        for (int y = 106; y < 126; y++)
        {
            float t = (y - 106) / 20f;
            int hw = (int)Mathf.Lerp(12f, 16f, t);
            Fill(cx - hw, y, cx + hw, y + 1, top);
        }
        Fill(cx - 16, 106, cx + 16, 108, topSh);                   
        Fill(cx - 23, 108, cx - 16, 126, top); Fill(cx + 16, 108, cx + 23, 126, top); 
        Fill(cx - 23, 124, cx - 16, 126, tie); Fill(cx + 16, 124, cx + 23, 126, tie);
        Fill(cx - 23, 108, cx - 16, 110, tie); Fill(cx + 16, 108, cx + 23, 110, tie);
        Fill(cx - 16, 110, cx - 12, 124, skin); Fill(cx + 12, 110, cx + 16, 124, skin); 

        for (int y = 106; y < 122; y++) Fill(cx - 2, y, cx + 3, y + 1, tie);
        Fill(cx - 4, 120, cx + 5, 124, tie);                       
        Fill(cx - 16, 121, cx - 4, 127, tie); Fill(cx + 5, 121, cx + 16, 127, tie); 

        Fill(cx - 4, 124, cx + 4, 130, skin);
        Disc(cx, 141, 19, skin);

        for (int y = -19; y <= 19; y++) for (int x = -19; x <= 19; x++)
            if (x * x + y * y <= 19 * 19 && (141 + y) >= 145) Px(cx + x, 141 + y, hair);
        Px(cx, 147, skin); Px(cx - 1, 146, skin); Px(cx + 1, 146, skin); 
        Ell(cx - 18, 140, 5, 15, hair); Ell(cx + 18, 140, 5, 15, hair);
        Fill(cx - 1, 161, cx + 2, 168, hair); Px(cx + 2, 168, hair);     

        Color eyeLo = Color.HSVToRGB(0.49f, 0.45f, 0.92f); 
        for (int sgn = -1; sgn <= 1; sgn += 2)
        {
            int ex = cx + sgn * 8;
            Ell(ex, 135, 4, 6, Color.white);                 
            Ell(ex, 134, 3, 5, eye);                         
            Ell(ex, 132, 3, 3, eyeLo);                       
            Ell(ex, 134, 2, 3, line);                        
            Disc(ex - 1, 137, 2, Color.white);               
            Px(ex + 1, 131, eyeHi); Px(ex + 2, 131, eyeHi);  
            Fill(ex - 4, 139, ex + 5, 141, line);            
            Px(ex + sgn * 4, 138, line); Px(ex + sgn * 5, 138, line); 
            Px(ex - 4, 132, line);                           
            Fill(ex - 4, 143, ex + 4, 144, hairSh);          
        }

        Ell(cx - 12, 132, 3, 2, blush); Ell(cx + 12, 132, 3, 2, blush);
        Fill(cx - 1, 130, cx + 2, 131, line);

        Fill(8, 169, 54, 185, new Color(1f, 1f, 1f, 1f));
        Fill(8, 183, 54, 185, teal);
        for (int k = 0; k < 4; k++) Fill(12 + k * 10, 173, 12 + k * 10 + 6, 182, teal);

        Disc(106, 175, 12, badge);
        Fill(99, 173, 113, 177, new Color(1f, 1f, 1f, 0.95f));

        Fill(10, 12, w - 10, 40, new Color(0.98f, 0.98f, 1f));
        Fill(10, 38, w - 10, 40, accent);
        for (int k = 0; k < 6; k++) Fill(18 + k * 16, 27, 18 + k * 16 + 10, 35, new Color(0.30f, 0.30f, 0.36f));
        for (int k = 0; k < 9; k++) Fill(16 + k * 11, 18, 16 + k * 11 + 6, 23, new Color(0.50f, 0.50f, 0.56f));

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float dd = Mathf.Abs((x - 0.45f * y) + 5f);
                if (dd < 7f)
                {
                    float a = (1f - dd / 7f) * 0.10f;
                    px[y * w + x] = Color32.Lerp(px[y * w + x], new Color32(255, 255, 255, 255), a);
                }
            }

        Color brd = Color.HSVToRGB(0.50f, 0.25f, 0.60f);
        Fill(0, 0, w, 4, brd); Fill(0, h - 4, w, h, brd); Fill(0, 0, 4, h, brd); Fill(w - 4, 0, w, h, brd);
        Color inb = accent;
        Fill(7, 7, w - 7, 9, inb); Fill(7, h - 9, w - 7, h - 7, inb); Fill(7, 7, 9, h - 7, inb); Fill(w - 9, 7, w - 7, h - 7, inb);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float ex = Mathf.Min(x, w - 1 - x) / (w * 0.5f);
                float ey = Mathf.Min(y, h - 1 - y) / (h * 0.5f);
                float wear = Mathf.Clamp01(1f - Mathf.Min(ex, ey) * 5f) * 0.13f;
                if (wear > 0.001f)
                {
                    var c = px[y * w + x];
                    px[y * w + x] = new Color32((byte)(c.r * (1 - wear)), (byte)(c.g * (1 - wear)), (byte)(c.b * (1 - wear)), c.a);
                }
            }
        for (int s = 0; s < 22; s++)
        {
            int X = rnd.Next(0, w), Y = rnd.Next(0, h);
            var c = px[Y * w + X];
            px[Y * w + X] = new Color32((byte)(c.r * 0.86f), (byte)(c.g * 0.86f), (byte)(c.b * 0.86f), c.a);
        }

        tex.SetPixels32(px);
        tex.Apply();
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }

    void BuildCamera()
    {
        var camGo = GameObject.Find("Main Camera");
        if (camGo == null)
            camGo = new GameObject("Main Camera");
            
        camGo.tag = "MainCamera"; 
        cam = camGo.GetComponent<Camera>();
        if (cam == null)
            cam = camGo.AddComponent<Camera>();
            
        cam.fieldOfView = 50f;
        cam.nearClipPlane = 0.3f;   // 💡 near↑ + far↓ → near/far 비율 최소화로 WebGL 깊이 정밀도 확보
        cam.farClipPlane = 40f;     //    (카메라 최소거리 2.0이라 0.3에서도 아무것도 안 잘림)
        cam.backgroundColor = new Color(0.043f, 0.055f, 0.078f);
        cam.clearFlags = CameraClearFlags.SolidColor;
    }

    void BuildLights()
    {
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)) 
            Destroy(l.gameObject);
            
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.34f, 0.36f, 0.44f);

        // 💡 WebGL 기본 품질은 픽셀 광원 수가 적어(1~4개), 우리 점광원 대부분이 꺼진 채 빌드됨
        //    → 씬이 평평하고 어둡게 보이는 원인. 넉넉히 올려서 조명이 실제로 켜지게 함.
        QualitySettings.pixelLightCount = 8;
        QualitySettings.shadowDistance = 30f;
        QualitySettings.shadows = ShadowQuality.All;
        QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
        var key = new GameObject("KeyLight").AddComponent<Light>();
        key.type = LightType.Directional;
        key.intensity = 0.3f;
        key.color = new Color(1f, 0.98f, 0.93f);
        key.shadows = LightShadows.Soft;
        key.shadowStrength = 0.7f;
        key.shadowBias = 0.02f; key.shadowNormalBias = 0.2f;
        key.transform.rotation = Quaternion.Euler(38f, -42f, 0f); 

        // BuildLights() 함수 내부의 리플렉션 프로브 설정부
        var rp = new GameObject("ReflProbe").AddComponent<ReflectionProbe>();
        
        // 💡 [조정] Z축 위치를 더 안쪽(0.0f)으로, Y축도 중앙으로 이동
        rp.transform.position = new Vector3(0f, H * 0.5f, 0.0f); 
        
        // 💡 [조정] 기기 내부 영역만 딱 맞게 반사하도록 크기를 대폭 축소
        rp.size = new Vector3(W * 1.8f, H * 0.9f, D * 1.8f); 
        
        rp.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
        // ... (나머지 설정 유지)

        rp.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
        rp.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces;
        rp.resolution = 128; rp.boxProjection = true; rp.intensity = 0.22f;
        rp.RenderProbe();

        void TopLight(string n, Vector3 pos, Color col, float inten)
        {
            var g = new GameObject(n).AddComponent<Light>();
            g.type = LightType.Point; g.color = col; g.intensity = inten; g.range = 6.5f;
            g.transform.position = pos;
        }
        Color warm = new Color(1f, 0.98f, 0.94f), cool = new Color(0.94f, 0.96f, 1f);
        TopLight("TopFL", new Vector3(-W * 0.55f, H - 0.15f, D - 0.1f), warm, 0.5f);
        TopLight("TopFR", new Vector3(W * 0.55f, H - 0.15f, D - 0.1f), warm, 0.5f);
        TopLight("TopBL", new Vector3(-W * 0.55f, H - 0.15f, -D + 0.1f), cool, 0.45f);
        TopLight("TopBR", new Vector3(W * 0.55f, H - 0.15f, -D + 0.1f), cool, 0.45f);
        // 기존 조명 설정 아래에 추가하거나 수정하세요
        // 보조 광원을 약간 더 앞쪽(플레이어 방향)으로 옮기고 밝기를 높입니다.
        TopLight("MidFill", new Vector3(0f, 0.8f, PlayZc-1.0f), new Color(0.96f, 0.97f, 1f), 0.3f);

        TopLight("NeonPink", new Vector3(-W + 0.04f, 1.4f, D - 0.1f), new Color(1f, 0.45f, 0.72f), 0.13f);
        TopLight("NeonCyan", new Vector3(W - 0.04f, 1.4f, D - 0.1f), new Color(0.45f, 0.82f, 1f), 0.18f);

        TopLight("Fill", new Vector3(0f, 1.1f, 2.2f), new Color(0.82f, 0.86f, 1f), 0.25f);
    }

    void BuildCabinet()
    {
        var root = new GameObject("Cabinet").transform;
        Cube("Base", new Vector3(0, -0.5f, 0), new Vector3(W * 2 + 0.18f, 0.95f, D * 2 + 0.18f), mWhite, false, root);
        Cube("Foot", new Vector3(0, -0.965f, 0), new Vector3(W * 2 + 0.22f, 0.09f, D * 2 + 0.22f), mPink, false, root);

        foreach (var dx in new[] { -0.4f })
        {
            Cube("Door", new Vector3(dx, -0.45f, D + 0.10f), new Vector3(0.46f, 0.36f, 0.02f), mPink, false, root);
            Cube("DoorWin", new Vector3(dx, -0.45f, D + 0.112f), new Vector3(0.36f, 0.26f, 0.006f), mDark, false, root);
        }

        Cube("GlassN", new Vector3(0, H / 2, -D), new Vector3(W * 2, H, 0.02f), mGlass, true, root);
        Cube("GlassS", new Vector3(0, H / 2, D), new Vector3(W * 2, H, 0.02f), mGlass, true, root);
        Cube("GlassW", new Vector3(-W, H / 2, 0), new Vector3(0.02f, H, D * 2), mGlass, true, root);
        Cube("GlassE", new Vector3(W, H / 2, 0), new Vector3(0.02f, H, D * 2), mGlass, true, root);
        Cube("GlassTop", new Vector3(0, H, 0), new Vector3(W * 2, 0.02f, D * 2), mGlass, false, root);

        foreach (var c in new[] { new Vector2(-W, -D), new Vector2(W, -D), new Vector2(-W, D), new Vector2(W, D) })
            Cube("Post", new Vector3(c.x, H / 2, c.y), new Vector3(0.045f, H, 0.045f), mWhite, false, root);

        foreach (var yy in new[] { 0.02f, H })
        {
            Cube("TrimN", new Vector3(0, yy, -D), new Vector3(W * 2, 0.05f, 0.05f), mPink, false, root);
            Cube("TrimS", new Vector3(0, yy, D), new Vector3(W * 2, 0.05f, 0.05f), mPink, false, root);
            Cube("TrimW", new Vector3(-W, yy, 0), new Vector3(0.05f, 0.05f, D * 2), mPink, false, root);
            Cube("TrimE", new Vector3(W, yy, 0), new Vector3(0.05f, 0.05f, D * 2), mPink, false, root);
        }

        float my = H - 0.16f;
        MakeMarqueeText(root, my);

        // 1. 네온 기둥(Cube) 자체의 밝기: 기존 7.2f에서 3.0f 정도로 조정
        var neon = Emissive(new Color(0.35f, 0.85f, 1f), new Color(0.5f, 2.0f, 3.0f), 0.2f, 0.7f);
        float fz = D + 0.015f; 
        Cube("NeonL", new Vector3(-W, H * 0.5f, fz), new Vector3(0.03f, H, 0.03f), neon, false, root);
        Cube("NeonR", new Vector3(W, H * 0.5f, fz), new Vector3(0.03f, H, 0.03f), neon, false, root);
        Cube("NeonT", new Vector3(0, H, fz), new Vector3(W * 2 + 0.03f, 0.03f, 0.03f), neon, false, root);
        Cube("NeonB", new Vector3(0, 0.05f, fz), new Vector3(W * 2 + 0.03f, 0.03f, 0.03f), neon, false, root);

        // 2. 주변으로 퍼지는 광원(Point Light): intensity를 0.5f로 대폭 낮춰서 '번짐' 현상 제거
        foreach (var p in new[] { new Vector3(-W, H * 0.7f, fz), new Vector3(W, H * 0.7f, fz), new Vector3(-W, H * 0.25f, fz), new Vector3(W, H * 0.25f, fz) })
        {
            var g = new GameObject("NeonGlow").AddComponent<Light>();
            g.type = LightType.Point; 
            g.color = new Color(0.4f, 0.85f, 1f);
            g.intensity = 0.5f; // [수정] 밝기를 3.0f -> 0.5f로 대폭 낮춤
            g.range = 0.8f;     // [수정] 범위를 2.0f -> 0.8f로 줄여서 네온 기둥 주위에만 살짝 맺히게 함
            g.transform.position = p;
        }

        GlowQuad("EdgeGlowL", new Vector3(-W, H * 0.5f, D + 0.02f), new Vector2(0.22f, H * 0.96f), new Color(0.14f, 0.36f, 0.5f), root);
        GlowQuad("EdgeGlowR", new Vector3(W, H * 0.5f, D + 0.02f), new Vector2(0.22f, H * 0.96f), new Color(0.14f, 0.36f, 0.5f), root);

        BuildControlPanel(root);
    }

    void MakeMarqueeText(Transform root, float my)
    {
        var tgo = new GameObject("UFOText");
        tgo.transform.SetParent(root, false);
        var tm = tgo.AddComponent<TextMesh>();
        var font = LoadUIFont();
        tm.font = font;
        tgo.GetComponent<MeshRenderer>().sharedMaterial = font.material; 
        tm.text = "MIND CATCHER";
        tm.fontSize = 45;
        tm.characterSize = 0.042f;
        tm.fontStyle = FontStyle.Bold;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = new Color(1f, 0.22f, 0.55f); 
        tgo.transform.position = new Vector3(0f, my, D - 0.025f);
        tgo.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

        var g = new GameObject("MarqueeGlow").AddComponent<Light>();
        g.transform.SetParent(root, false);
        g.type = LightType.Point; g.color = new Color(1f, 0.3f, 0.6f);
        g.intensity = 2.2f; g.range = 1.6f; g.transform.position = new Vector3(0f, my, D - 0.18f);

        GlowQuad("MarqueeGlowQuad", new Vector3(0f, my, D - 0.05f), new Vector2(W * 1.5f, 0.36f),
                 new Color(0.62f, 0.14f, 0.34f), root);
    }

    Texture2D _glowTex;
    Texture2D GlowTex()
    {
        if (_glowTex != null) return _glowTex;
        int s = 64;
        var t = new Texture2D(s, s, TextureFormat.RGBA32, false);
        var px = new Color32[s * s];
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float dx = (x + 0.5f) / s - 0.5f, dy = (y + 0.5f) / s - 0.5f;
                float d = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) * 2f);
                float a = 1f - d; a *= a; 
                px[y * s + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        t.SetPixels32(px); t.Apply(); t.wrapMode = TextureWrapMode.Clamp;
        _glowTex = t; return t;
    }

    Material AdditiveGlow(Color tint)
    {
        var sh = Shader.Find("Legacy Shaders/Particles/Additive");
        if (sh == null) sh = Shader.Find("Particles/Additive");
        if (sh == null) sh = Shader.Find("Mobile/Particles/Additive");
        var m = new Material(sh != null ? sh : Shader.Find("Sprites/Default"));
        m.mainTexture = GlowTex();
        if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", tint);
        m.color = tint;
        return m;
    }

    void GlowQuad(string n, Vector3 pos, Vector2 size, Color tint, Transform parent)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = n; Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = AdditiveGlow(tint);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }

    void BuildControlPanel(Transform root)
    {
        float pz = D + 0.18f;  
        float py = -0.12f;     

        Cube("Panel", new Vector3(0, py, pz - 0.06f), new Vector3(W * 1.85f, 0.05f, 0.26f), mWhite, false, root);
        Cube("PanelFront", new Vector3(0, py - 0.05f, pz + 0.05f), new Vector3(W * 1.85f, 0.06f, 0.02f), mPink, false, root);

        Cube("Terminal", new Vector3(0, py + 0.05f, pz - 0.02f), new Vector3(0.24f, 0.16f, 0.12f), mDark, false, root);
        Cube("Screen", new Vector3(0, py + 0.07f, pz + 0.045f), new Vector3(0.17f, 0.1f, 0.01f),
             Emissive(new Color(0.2f, 0.55f, 0.75f), new Color(0.1f, 0.45f, 0.7f)), false, root);
        Cube("CardSlot", new Vector3(0, py - 0.02f, pz + 0.05f), new Vector3(0.11f, 0.015f, 0.01f), mPink, false, root);

        var btnRed = Emissive(new Color(0.92f, 0.2f, 0.3f), new Color(0.5f, 0.06f, 0.1f));
        var btnPink = Emissive(new Color(1f, 0.45f, 0.65f), new Color(0.5f, 0.15f, 0.25f));
        void Btn(float x, Material m)
            => Cyl("Btn", new Vector3(x, py + 0.03f, pz + 0.01f), 0.04f, 0.035f, m, false, root);
        Btn(-0.34f, btnPink); Btn(-0.46f, btnRed);
    }

    void BuildCommonFloor()
    {
        var root = new GameObject("Environment").transform;
        
        void Slab(float cx, float cz, float hx, float hz) 
        {
            var go = Cube("Floor", new Vector3(cx, -0.05f, cz), new Vector3(hx * 2, 0.1f, hz * 2), mFloor, true, root);
            SetPM(go, pmFloor);
        }
        
        Slab(0f, (PlayZc + 0.2f + D) * 0.5f, W, (D - PlayZc - 0.2f) * 0.5f);  
        Slab(0f, (-D + PlayZc - 0.2f) * 0.5f, W, (D + PlayZc - 0.2f) * 0.5f); 
        Slab(-0.625f, PlayZc, 0.225f, 0.2f);                                 
        Slab(0.625f, PlayZc, 0.225f, 0.2f);                                  

        var invis = Glass(Color.white, 0f);
        Cube("WallN", new Vector3(0, 0.5f, -D), new Vector3(W * 2, 1f, 0.02f), invis, true, root).GetComponent<MeshRenderer>().enabled = false;
        Cube("WallS", new Vector3(0, 0.5f, D), new Vector3(W * 2, 1f, 0.02f), invis, true, root).GetComponent<MeshRenderer>().enabled = false;
        Cube("WallW", new Vector3(-W, 0.5f, 0), new Vector3(0.02f, 1f, D * 2), invis, true, root).GetComponent<MeshRenderer>().enabled = false;
        Cube("WallE", new Vector3(W, 0.5f, 0), new Vector3(0.02f, 1f, D * 2), invis, true, root).GetComponent<MeshRenderer>().enabled = false;

        Cube("Collector", new Vector3(0f, -0.62f, PlayZc), new Vector3(W * 2, 0.08f, 0.8f), mDark, true, root);

        float hx = 0.4f, z0 = PlayZc - 0.2f, z1 = PlayZc + 0.2f, zc = PlayZc;
        var chuteMat = Std(new Color(0.05f, 0.05f, 0.07f), 0.2f, 0.25f); 
        Cube("ChuteF", new Vector3(0f, -0.33f, z0), new Vector3(hx * 2 + 0.02f, 0.54f, 0.02f), chuteMat, false, root);
        Cube("ChuteB", new Vector3(0f, -0.33f, z1), new Vector3(hx * 2 + 0.02f, 0.54f, 0.02f), chuteMat, false, root);
        Cube("ChuteL", new Vector3(-hx, -0.33f, zc), new Vector3(0.02f, 0.54f, 0.4f), chuteMat, false, root);
        Cube("ChuteR", new Vector3(hx, -0.33f, zc), new Vector3(0.02f, 0.54f, 0.4f), chuteMat, false, root);

        var lipMat = Std(new Color(0.10f, 0.10f, 0.13f), 0.4f, 0.45f);
        Cube("LipF", new Vector3(0f, -0.02f, z0), new Vector3(hx * 2 + 0.08f, 0.05f, 0.05f), lipMat, false, root);
        Cube("LipB", new Vector3(0f, -0.02f, z1), new Vector3(hx * 2 + 0.08f, 0.05f, 0.05f), lipMat, false, root);
        Cube("LipL", new Vector3(-hx, -0.02f, zc), new Vector3(0.05f, 0.05f, 0.44f), lipMat, false, root);
        Cube("LipR", new Vector3(hx, -0.02f, zc), new Vector3(0.05f, 0.05f, 0.44f), lipMat, false, root);

        var rim = Emissive(new Color(0.3f, 0.85f, 1f), new Color(0.25f, 1.1f, 1.7f), 0.2f, 0.7f);
        Cube("RimF", new Vector3(0f, 0.005f, z0), new Vector3(hx * 2 + 0.02f, 0.015f, 0.02f), rim, false, root);
        Cube("RimB", new Vector3(0f, 0.005f, z1), new Vector3(hx * 2 + 0.02f, 0.015f, 0.02f), rim, false, root);
        Cube("RimL", new Vector3(-hx, 0.005f, zc), new Vector3(0.02f, 0.015f, 0.4f), rim, false, root);
        Cube("RimR", new Vector3(hx, 0.005f, zc), new Vector3(0.02f, 0.015f, 0.4f), rim, false, root);

        var arrow = Emissive(new Color(1f, 0.4f, 0.65f), new Color(0.6f, 0.12f, 0.28f), 0f, 0.4f);
        Cube("ChuteFloor", new Vector3(0f, -0.575f, zc), new Vector3(hx * 1.6f, 0.01f, 0.28f), arrow, false, root);
        var gl = new GameObject("ChuteGlow").AddComponent<Light>();
        gl.type = LightType.Point; gl.color = new Color(0.4f, 0.85f, 1f);
        gl.intensity = 1.2f; gl.range = 1.0f; gl.transform.position = new Vector3(0f, -0.2f, zc);
    }

    void BuildClaw()
    {
        pole = Cyl("Pole", new Vector3(0f, 1.0f, 0f), 0.015f, 1f, mSilver, false).transform;
        
        var headGo = new GameObject("Trolley");
        trolleyRb = headGo.AddComponent<Rigidbody>(); 
        trolleyRb.isKinematic = true; 
        trolleyRb.interpolation = RigidbodyInterpolation.Interpolate;
        
        clawHead = headGo.transform; 
        clawHead.position = clawPos;
        
        var h1 = Cube("Housing", Vector3.zero, new Vector3(0.22f, 0.10f, 0.14f), mWhite, false, clawHead);
        h1.transform.localPosition = new Vector3(0, 0.02f, 0);
        
        var h2 = Cube("HousingTop", Vector3.zero, new Vector3(0.18f, 0.04f, 0.10f), mSilver, false, clawHead);
        h2.transform.localPosition = new Vector3(0, 0.09f, 0);
        
        var collar = Cube("Collar", Vector3.zero, new Vector3(0.10f, 0.05f, 0.10f), mPink, false, clawHead);
        collar.transform.localPosition = new Vector3(0, -0.055f, 0);
        
        Sphere("BtnR", new Vector3(-0.05f, 0.02f, 0.07f), 0.02f, Emissive(new Color(0.9f, 0.2f, 0.2f), new Color(0.3f, 0.02f, 0.02f)), clawHead);
        Sphere("BtnB", new Vector3(0f, 0.02f, 0.07f), 0.02f, Emissive(new Color(0.2f, 0.4f, 0.95f), new Color(0.02f, 0.05f, 0.3f)), clawHead);
        Sphere("BtnY", new Vector3(0.05f, 0.02f, 0.07f), 0.02f, Emissive(new Color(0.95f, 0.8f, 0.2f), new Color(0.3f, 0.22f, 0.02f)), clawHead);

        foreach (var side in new[] { -1f, 1f }) 
        {
            prongs.Add(MakeProng(side));
        }

        List<Collider> clawCols = new List<Collider>(clawHead.GetComponentsInChildren<Collider>());
        foreach(var p in prongs) 
        {
            clawCols.AddRange(p.rb.gameObject.GetComponentsInChildren<Collider>());
        }
        for (int i = 0; i < clawCols.Count; i++) 
        {
            for (int j = i + 1; j < clawCols.Count; j++) 
            {
                Physics.IgnoreCollision(clawCols[i], clawCols[j]);
            }
        }
        
        ApplyGripTargets(); 
        PositionClaw(0f);
    }

    void Segment(Transform parent, Vector3 a, Vector3 b, float thick, PhysicsMaterial pm)
    {
        Vector3 c = (a + b) * 0.5f; 
        Vector3 d = b - a; 
        float len = d.magnitude; 
        d /= Mathf.Max(len, 1e-4f);
        
        var go = Cube("Seg", Vector3.zero, new Vector3(thick, len, thick), mSilver, true, parent);
        go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, d); 
        go.transform.localPosition = c;
        go.GetComponent<Collider>().sharedMaterial = pm;
    }

    Prong MakeProng(float side)
    {
        var go = new GameObject(side > 0 ? "Prong_R" : "Prong_L");
        Vector3 pivot = clawPos + new Vector3(side * PivotX, -0.05f, 0);
        go.transform.position = pivot;

        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 0.1f; 
        rb.angularDamping = 1.5f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        Vector3 elbow = new Vector3(side * 0.06f, -0.18f, 0); 
        Vector3 tip = new Vector3(side * 0.06f, -0.34f, 0); 
        
        Segment(go.transform, Vector3.zero, elbow, 0.02f, pmSlip);  
        Segment(go.transform, elbow, tip, 0.025f, pmClaw);          
        
        Sphere("Knuckle", elbow, 0.022f, mWhite, go.transform);
        Sphere("Tip", tip, 0.015f, mRedTip, go.transform); 

        var hj = go.AddComponent<HingeJoint>();
        hj.connectedBody = trolleyRb;
        hj.autoConfigureConnectedAnchor = false;
        hj.anchor = Vector3.zero;
        hj.connectedAnchor = new Vector3(side * PivotX, -0.05f, 0);
        hj.axis = Vector3.forward;
        hj.useLimits = true; 
        hj.useMotor = true; 
        hj.enableCollision = false;
        hj.enablePreprocessing = false; 

        return new Prong { rb = rb, hj = hj, side = side };
    }

    void ApplyGripTargets()
    {
        foreach (var p in prongs) 
        {
            float openA = p.side * openAngle; 
            float closeA = p.side * closeAngle;
            
            p.hj.limits = new JointLimits 
            { 
                min = Mathf.Min(openA, closeA), 
                max = Mathf.Max(openA, closeA), 
                bounciness = 0f 
            };
            
            float vel = (gripClosed ? -1f : 1f) * p.side * motorSpeed;
            // 💡 [들어올림 원천 방지] 올라감/이동/오픈 단계에선 악력을 5N으로 제한.
            //    패드 마찰 상한(0.4)과 결합 → 최대 유지력 2×5×0.4=4N < 박스 무게(≈9.8N)
            //    → 아무리 슬라이더를 올려도 박스를 공중으로 못 들어올림(밀기는 그대로 강함).
            float f = gripForce;
            if (state == State.Drop && (phase == Phase.Ascend || phase == Phase.Move || phase == Phase.Open))
                f = Mathf.Min(f, 5f);
            p.hj.motor = new JointMotor
            {
                force = f,
                targetVelocity = vel,
                freeSpin = false
            };
        }
    }

    Prize MakeBox(Vector3 pos, Vector3 half, Material bodyMat, float mass)
    {
        var root = new GameObject("Prize");
        root.transform.position = pos;
        var bc = root.AddComponent<BoxCollider>();
        bc.size = half * 2f;
        bc.sharedMaterial = pmPrize;

        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body"; Destroy(body.GetComponent<Collider>());
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = half * 2f;
        body.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;

        var rb = root.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.angularDamping = 0.35f;
        rb.linearDamping = 0.03f;
        rb.centerOfMass = Vector3.zero;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        var p = new Prize { go = root, rb = rb, half = half };
        prizes.Add(p);
        return p;
    }

    void BuildBridge()
    {
        // 💡 리셋 연타 시 봉이 겹겹이 쌓이는 버그 방지: 이전 세트 먼저 제거
        if (railsRoot != null) Destroy(railsRoot.gameObject);
        var root = new GameObject("Rails").transform;
        railsRoot = root;
        
        float railY = 0.5f;
        float railR = 0.018f;
        float railLen = 1.7f; 
        
        float inner = Mathf.Clamp(railGap, 0.08f, 0.4f) * 0.43f;
        // 💡 [승리 가능 조건] 봉 실틈이 박스 두께(0.2)보다 '살짝만 넓게' 유지되도록 클램프.
        //    → 박스를 수직 가까이 세우면 틈으로 빠짐. 틈이 좁을수록 더 정확히 세워야 함(어려움).
        //    railGap 슬라이더: 줄이면 어려움 ↔ 키우면 쉬움.
        inner = Mathf.Clamp(inner, 0.125f, 0.145f); // 실틈 ≈ 0.214 ~ 0.254
        float outer = inner + 0.16f;
        
        float backY = railY;
        float frontY = railY;
        float innerY = railY - 0.06f; 

        var r1 = Cyl("Rail_InnerBack", new Vector3(0f, innerY, inner), railR, railLen, mPink, false, root);
        r1.transform.rotation = Quaternion.Euler(0f, 0f, 90f); 
        var r2 = Cyl("Rail_InnerFront", new Vector3(0f, innerY, -inner), railR, railLen, mPink, false, root);
        r2.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
        var r5 = Cyl("Rail_OuterBack", new Vector3(0f, backY, outer), railR, railLen, mRail, false, root);
        r5.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
        var r6 = Cyl("Rail_OuterFront", new Vector3(0f, frontY, -outer), railR, railLen, mRail, false, root);
        r6.transform.rotation = Quaternion.Euler(0f, 0f, 90f);

        void RailCol(Vector3 pos)
        {
            var c = Cube("RailCol", pos, new Vector3(railLen, railR * 2f, railR * 2f), mRail, true, root);
            c.GetComponent<MeshRenderer>().enabled = false; 
            SetPM(c, pmRail);
        }
        RailCol(new Vector3(0f, innerY, inner));
        RailCol(new Vector3(0f, innerY, -inner));
        RailCol(new Vector3(0f, backY, outer));
        RailCol(new Vector3(0f, frontY, -outer));

        float slopeAngle = Mathf.Atan2(backY - frontY, outer * 2f) * Mathf.Rad2Deg; 
        
        var mountL = Cube("RailMountLeft", new Vector3(-0.805f, railY, 0f), new Vector3(0.04f, 0.05f, outer * 2f + 0.12f), mRail, false, root);
        mountL.transform.rotation = Quaternion.Euler(slopeAngle, 0f, 0f);
        
        var mountR = Cube("RailMountRight", new Vector3(0.805f, railY, 0f), new Vector3(0.04f, 0.05f, outer * 2f + 0.12f), mRail, false, root);
        mountR.transform.rotation = Quaternion.Euler(slopeAngle, 0f, 0f);

        root.localPosition = new Vector3(0f, 0f, PlayZc); 

        float boxX = boxWidthX * 0.5f;
        float thickHalf = boxHeightY * 0.5f;
        float boxZ = boxDepthZ * 0.5f;

        bridgeMode = true;
        // 💡 스폰: 실제 기기 사진처럼 — 박스가 '평평하게 누워' 두 봉에 다리처럼 걸쳐 있고
        //    그림 면이 위를 향한 상태. 긴 변(0.38)이 봉 틈(≈0.22)을 가로질러 안정적으로 걸침.
        bridgeBoxPos = new Vector3(0f, innerY + railR + thickHalf + 0.02f, PlayZc);
        bridgeBoxHalf = new Vector3(boxX, thickHalf, boxZ);
        SpawnBridgeBox();

        modeName = "하시와타시 (완만한 경사 & 타이트 틈새)";
        descendY = 0.87f; // 💡 집게 하강 깊이(작을수록 깊이 내려감). 프롱 팁이 봉 살짝 위(~0.49)까지 닿음
    }

    void SpawnBridgeBox()
    {
        var box = MakeBox(bridgeBoxPos, bridgeBoxHalf, SideMat(), boxMass); 
        AddFrontCard(box.go.transform, bridgeBoxHalf * 2f, 1, FigMat(3,true));
        box.rb.centerOfMass = new Vector3(0f, 0f, boxComOffset);
        // 💡 실제 기기처럼 '누운' 자세: 긴 변(로컬X 0.38)→앞뒤(봉 틈 가로지름),
        //    미쿠 카드 면(로컬Y)→위. 앞/뒤 가장자리를 눌러 세워서 틈으로 떨어뜨리는 공략.
        box.go.transform.rotation = Quaternion.Euler(0f, -90f, 0f);
        box.rb.rotation = box.go.transform.rotation;
        box.rb.angularDamping = 0.08f;   // 💡 낮춤 → 집게로 밀면 잘 기울어짐(박스 세우기=타테하메 쉬워짐)
        box.rb.maxAngularVelocity = 9f;
        box.rb.maxDepenetrationVelocity = 1f;
        box.rb.linearVelocity = Vector3.zero;
        box.rb.angularVelocity = Vector3.zero;

        // 💡 스폰 직후 잠깐 고정(kinematic) → 로딩 직후 튀는 첫 프레임(들)의 물리 캐치업으로
        //    봉을 관통하는 걸 방지. 짧게 굳혔다가 안전하게 물리를 켠다.
        box.rb.isKinematic = true;
        bridgeRb = box.rb;
        settleT = 0.35f;
    }

    void StartMode()
    {
        ClearPrizes();
        pmRail.dynamicFriction = railFriction;
        pmRail.staticFriction = railFriction + 0.05f;
        pmPrize.dynamicFriction = prizeFriction;
        pmPrize.staticFriction = prizeFriction + 0.1f;
        pmClaw.dynamicFriction = Mathf.Min(padFriction, 0.4f);          // 💡 들어올림 방지: 패드 마찰 상한
        pmClaw.staticFriction = Mathf.Min(padFriction, 0.4f) + 0.05f;

        bridgeMode = false;
        BuildBridge();

        SnapClaw(HomePos);        // 💡 트롤리+프롱을 함께 순간이동(조인트 당김으로 팔 튕기는 버그 방지)
        gripClosed = true;
        timeLeft = TimeLimit;
        wonThisDrop = false;
        state = State.Aim;
        msg = "조준하세요";
    }

    // 집게(트롤리+프롱)를 조인트 스트레칭 없이 통째로 순간이동
    void SnapClaw(Vector3 pos)
    {
        clawPos = pos;
        if (trolleyRb != null) { trolleyRb.position = pos; clawHead.position = pos; }
        foreach (var p in prongs)
        {
            if (p.rb == null) continue;
            p.rb.position = pos + new Vector3(p.side * PivotX, -0.05f, 0f);
            p.rb.rotation = Quaternion.identity;
            p.rb.linearVelocity = Vector3.zero;
            p.rb.angularVelocity = Vector3.zero;
        }
    }

    
    void ClearPrizes() 
    { 
        foreach (var p in prizes) 
        {
            if (p.go) 
            {
                Destroy(p.go); 
            }
        }
        prizes.Clear(); 
    }

    void DoGet() 
    { 
        if (state != State.Aim) 
        {
            return; 
        }
        
        if (coins <= 0)
        {
            msg = "코인이 없습니다";
            return;
        }

        coins--;
        coinsUsed++;        
        wonThisDrop = false;
        state = State.Drop;
        phase = Phase.Descend;
        phaseT = 0f;
        gripClosed = false;
        msg = "내려갑니다...";
        if (sfxSrc != null) sfxSrc.PlayOneShot(clipCoin); 
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        if (successFlash > 0f) successFlash -= dt;

        if (settleT > 0f)
        {
            settleT -= dt;
            if (settleT <= 0f && bridgeRb != null)
            {
                bridgeRb.isKinematic = false;
                bridgeRb.linearVelocity = Vector3.zero;
                bridgeRb.angularVelocity = Vector3.zero;
                bridgeRb = null;
            }
        }

        // 💡 [수정 1] 화면 UI 버튼에서 눌러진 상태(OnGUI의 입력)를 임시로 저장합니다.
        bool uiMoved = clawMoved; 
        
        // 💡 기존처럼 false로 초기화하되, 키보드 입력을 새로 받기 위해 비웁니다.
        clawMoved = false; 

        if (state == State.Aim)
        {
            UpdateAim(dt);
            if (coins > 0)
            {
                timeLeft -= dt;
                if (timeLeft <= 0f) { timeLeft = 0f; DoGet(); }
            }
            
            // 💡 [수정 2] 키보드(clawMoved)나 화면 버튼(uiMoved) 중 하나라도 눌렸으면 모터 소리를 켭니다.
            motorOn = clawMoved || uiMoved; 
        }
        else if (state == State.Drop)
        {
            UpdateDrop(dt);
            motorOn = (phase == Phase.Descend || phase == Phase.Ascend || phase == Phase.Move);
        }
        else motorOn = false;

        ApplyGripTargets();
        PositionClaw(dt);
        CheckCollect();

        if (bridgeMode && state == State.Aim && prizes.Count == 0) SpawnBridgeBox();
    }

    void UpdateAim(float dt)
    {
        if (coins <= 0)
        {
            msg = "코인이 없습니다";
            return;
        }

        var kb = Keyboard.current;

        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f; 
        fwd.Normalize(); 
        
        Vector3 right = cam.transform.right; 
        right.y = 0f; 
        right.Normalize(); 
        
        Vector3 mv = Vector3.zero;
        
        // 💡 [수정] 키보드 입력이거나 UI 버튼이 눌렸을 때 이동 벡터를 누적합니다.
        if ((kb != null && (kb.leftArrowKey.isPressed || kb.aKey.isPressed)) || uiLeft) mv -= right; 
        if ((kb != null && (kb.rightArrowKey.isPressed || kb.dKey.isPressed)) || uiRight) mv += right; 
        if ((kb != null && (kb.upArrowKey.isPressed || kb.wKey.isPressed)) || uiUp) mv += fwd; 
        if ((kb != null && (kb.downArrowKey.isPressed || kb.sKey.isPressed)) || uiDown) mv -= fwd;
        
        if (mv.sqrMagnitude > 1e-4f)
        {
            // dt(Time.fixedDeltaTime)를 곱하여 프레임과 무관하게 항상 일정한 속도로 이동합니다.
            mv = mv.normalized * (0.28f * dt); 
            clawPos.x = Mathf.Clamp(clawPos.x + mv.x, -PlayX, PlayX); 
            clawPos.z = Mathf.Clamp(clawPos.z + mv.z, ClawZBack, PlayZ);
            clawMoved = true; 
        }
    }

    void UpdateDrop(float dt)
    {
        phaseT += dt; 
        float speed = dropSpeed;
        
        switch (phase) 
        {
            case Phase.Descend:
                gripClosed = false; 
                clawPos.y = Mathf.Max(descendY, clawPos.y - speed * dt); 
                if (clawPos.y <= descendY + 1e-3f)
                {
                    phase = Phase.Close;
                    phaseT = 0f;
                    msg = "집는 중...";
                    if (sfxSrc != null) sfxSrc.PlayOneShot(clipClunk); 
                }
                break;
                
            case Phase.Close:
                gripClosed = true; 
                if (phaseT > 0.8f) 
                { 
                    phase = Phase.Ascend; 
                    phaseT = 0f; 
                    msg = "올라갑니다..."; 
                } 
                break;
                
            case Phase.Ascend:
                gripClosed = true; 
                clawPos.y = Mathf.Min(TopY, clawPos.y + speed * dt); 
                if (clawPos.y >= TopY - 1e-3f) 
                { 
                    phase = Phase.Move; 
                    phaseT = 0f; 
                } 
                break;
                
            case Phase.Move: 
                gripClosed = true;
                clawPos.x += (HomePos.x - clawPos.x) * Mathf.Min(1f, dt * 3f);
                clawPos.z += (HomePos.z - clawPos.z) * Mathf.Min(1f, dt * 3f);
                if (Mathf.Abs(clawPos.x - HomePos.x) < 0.01f && Mathf.Abs(clawPos.z - HomePos.z) < 0.01f)
                {
                    phase = Phase.Open;
                    phaseT = 0f;
                }
                break;

            case Phase.Open:
                gripClosed = true; 
                if (phaseT > 0.4f) { phase = Phase.Done; phaseT = 0f; }
                break;

            case Phase.Done:
                if (phaseT > 0.3f)
                {
                    if (!wonThisDrop)
                    {
                        failCount++;   
                        if (sfxSrc != null) sfxSrc.PlayOneShot(clipFail,0.3f); 
                    }
                    clawPos = HomePos;
                    gripClosed = true;               
                    timeLeft = TimeLimit;            
                    state = State.Aim;
                    msg = "조준하세요";
                    if (bridgeMode)
                    {
                        for (int i = prizes.Count - 1; i >= 0; i--)
                        {
                            var pr = prizes[i];
                            if (!pr.go || pr.rb.position.y < bridgeBoxPos.y - 0.25f)
                            {
                                if (pr.go) Destroy(pr.go);
                                prizes.RemoveAt(i);
                            }
                        }
                        if (prizes.Count == 0) SpawnBridgeBox();
                        pendingRespawn = false;
                    }
                }
                break;
        }
    }

    void PositionClaw(float dt)
    {
        trolleyRb.MovePosition(clawPos);
        float top = H - 0.08f; 
        float bot = clawPos.y + 0.08f;
        float h = Mathf.Max(0.02f, top - bot);
        pole.position = new Vector3(clawPos.x, (top + bot) * 0.5f, clawPos.z);
        pole.localScale = new Vector3(0.018f * 2f, h * 0.5f, 0.018f * 2f);
    }

    void CheckCollect()
    {
        for (int i = prizes.Count - 1; i >= 0; i--)
        {
            var p = prizes[i];
            if (!p.collected && p.go && p.rb.position.y < CollectY)
            {
                p.collected = true;
                Destroy(p.go);
                prizes.RemoveAt(i);
                if (state == State.Drop && !wonThisDrop)  
                {
                    collected++;
                    successCount++;
                    wonThisDrop = true;
                    msg = "획득 성공!";
                    SuccessEffect();
                }
            }
        }
    }

    void SuccessEffect()
    {
        successFlash = 1.6f;
        if (sfxSrc != null) sfxSrc.PlayOneShot(clipSuccess); 
        Color[] cc = { Color.yellow, Color.cyan, Color.magenta, new Color(0.4f, 1f, 0.4f), new Color(1f, 0.5f, 0.2f) };
        for (int i = 0; i < 16; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Confetti";
            go.transform.localScale = Vector3.one * (0.025f + Random.value * 0.02f);
            go.transform.position = new Vector3(Random.Range(-0.2f, 0.2f), 0.1f, Random.Range(-0.1f, 0.2f));
            go.GetComponent<MeshRenderer>().sharedMaterial = Std(cc[i % cc.Length], 0.1f, 0.5f);
            var rb = go.AddComponent<Rigidbody>();
            rb.linearVelocity = new Vector3(Random.Range(-1.2f, 1.2f), Random.Range(3f, 5f), Random.Range(-1.2f, 1.2f));
            rb.angularVelocity = Random.insideUnitSphere * 10f;
            Destroy(go, 2.2f);
        }
    }

    void Update()
    {
        var kb = Keyboard.current; 
        var mouse = Mouse.current;

        // 💡 [추가] 모바일 터치 입력을 받기 위한 변수
        var touch = Touchscreen.current;
        
        if (state != State.Menu && kb != null && kb.spaceKey.wasPressedThisFrame) 
        {
            DoGet();
        }
            
        if (mouse != null) 
        {
            if (mouse.rightButton.isPressed || mouse.middleButton.isPressed) 
            {
                Vector2 d = mouse.delta.ReadValue(); 
                camYaw += d.x * 0.18f; 
                camPitch = Mathf.Clamp(camPitch - d.y * 0.18f, 5f, 85f);
            }
            float scroll = mouse.scroll.ReadValue().y; 
            // 💡 최소거리를 캐비닛 바깥으로 확보 → 카메라가 네온판/유리를 뚫고 들어가는 것 방지
            camDist = Mathf.Clamp(camDist - scroll / 120f * 0.3f, 2.0f, 6f);
        }

        // ------------------------------------------------
        // 2. 💡 [추가] 모바일 터치 입력 처리 (시점 회전 및 줌)
        // ------------------------------------------------
        if (touch != null)
        {
            var t0 = touch.touches[0]; // 첫 번째 손가락
            var t1 = touch.touches[1]; // 두 번째 손가락

            // 👆 한 손가락 드래그 (시점 회전)
            // 주의: 하단 조작키(UI)를 누를 때 시점이 같이 돌아가는 것을 막기 위해,
            // 터치 시작 지점이 화면 위쪽(상위 65%)일 때만 시점이 돌아가도록 제한합니다.
            if (t0.press.isPressed && !t1.press.isPressed)
            {
                if (t0.startPosition.ReadValue().y > Screen.height * 0.35f)
                {
                    Vector2 d = t0.delta.ReadValue();
                    camYaw += d.x * 0.15f; 
                    camPitch = Mathf.Clamp(camPitch - d.y * 0.15f, 5f, 85f);
                }
            }
            // ✌️ 두 손가락 핀치 줌 (확대/축소)
            else if (t0.press.isPressed && t1.press.isPressed)
            {
                // 두 손가락의 이전 위치와 현재 위치의 거리를 비교
                Vector2 prevPos0 = t0.position.ReadValue() - t0.delta.ReadValue();
                Vector2 prevPos1 = t1.position.ReadValue() - t1.delta.ReadValue();
                
                float prevDist = Vector2.Distance(prevPos0, prevPos1);
                float curDist = Vector2.Distance(t0.position.ReadValue(), t1.position.ReadValue());
                
                // 거리가 벌어지면 확대, 좁혀지면 축소
                float diff = curDist - prevDist;
                camDist = Mathf.Clamp(camDist - diff * 0.015f, 2.0f, 6f);
            }
        }
        
        Quaternion rot = Quaternion.Euler(camPitch, camYaw, 0);
        // 💡 fieldOfView는 세로(수직) 기준 → 좁은/세로 화면(모바일 등)일수록 가로로 보이는 폭이
        //    확 줄어 "확대된 듯" 답답해짐. 화면이 세로일수록 카메라를 더 뒤로 물려서 보정.
        float aspect = (cam.pixelHeight > 0) ? cam.pixelWidth / (float)cam.pixelHeight : 1.6f;
        float extraDist = aspect < 1.2f ? Mathf.Lerp(1.6f, 0f, Mathf.InverseLerp(0.5f, 1.2f, aspect)) : 0f;
        cam.transform.position = camTarget + rot * new Vector3(0f, 0f, -(camDist + extraDist));
        cam.transform.LookAt(camTarget);

        if (motorSrc != null)
            motorSrc.volume = Mathf.MoveTowards(motorSrc.volume, motorOn ? 0.12f : 0f, Time.deltaTime * 2.5f);

        if (coinFlash > 0f) coinFlash -= Time.deltaTime;
    }

    GUIStyle _title, _label, _btn, _big, _timer, _banner;
    Font _kfont;

    Font _uiFont;
    Font LoadUIFont()
    {
        if (_uiFont != null) return _uiFont;
        _uiFont = Resources.Load<Font>("Fonts/korean");
        if (_uiFont == null) _uiFont = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Arial Unicode MS", "Gulim", "Dotum" }, 18);
        return _uiFont;
    }

    void EnsureStyles()
    {
        if (_title != null)
        {
            return;
        }
        _kfont = LoadUIFont();
        _title = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, font = _kfont };
        _label = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleLeft, font = _kfont };
        _btn = new GUIStyle(GUI.skin.button) { fontSize = 18, font = _kfont };
        _big = new GUIStyle(GUI.skin.button) { fontSize = 24, fontStyle = FontStyle.Bold, font = _kfont };
        _timer = new GUIStyle(GUI.skin.label) { fontSize = 44, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, font = _kfont };
        _banner = new GUIStyle(GUI.skin.label) { fontSize = 60, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, font = _kfont };
    }

    void OnGUI()
    {
        EnsureStyles();
        
        float scale = Mathf.Min(Screen.width / 1600f, Screen.height / 900f);
        
        // 💡 [수정] 모바일 기기이거나 세로 화면(Portrait)일 경우, 
        // 화면이 작으므로 UI 크기를 2.2배 강제로 뻥튀기합니다.
        if (Application.isMobilePlatform || Screen.height > Screen.width)
        {
            scale *= 2.4f; 
        }

        if (scale < 0.4f) scale = 0.4f; 
        
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        
        // 💡 실제 화면 비율의 끝단 좌표를 동적으로 획득 (sw, sh가 실제 모니터 우측/하단 끝을 가리킴)
        float sw = Screen.width / scale;
        float sh = Screen.height / scale;

        // -------------------------------------------------------------------------
        // 1. 상단 정보창 및 유틸리티 영역
        // -------------------------------------------------------------------------
        GUI.Box(new Rect(sw / 2f - 300f, 12f, 600f, 44f), "");
        GUI.Label(new Rect(sw / 2f - 285f, 12f, 600f, 44f),
            $"코인 {coins}   |   시도 {coinsUsed}   성공 {successCount}   실패 {failCount}   |   {msg}", _label);

        GUI.Label(new Rect(20f, 12f, 200f, 24f), "BUILD v15", _label);

        var prev = GUI.color;
        GUI.color = (timeLeft <= 4f) ? new Color(1f, 0.15f, 0.15f) : new Color(1f, 0.45f, 0.45f);
        GUI.Label(new Rect(sw - 150f, 64f, 130f, 56f), $"{Mathf.CeilToInt(timeLeft)}초", _timer);
        GUI.color = prev;

        if (GUI.Button(new Rect(sw - 120f, 12f, 100f, 44f), muted ? "소리 켜기" : "음소거", _btn))
        {
            muted = !muted;
            AudioListener.volume = muted ? 0f : 1f;
        }

        // -------------------------------------------------------------------------
        // 2. 🕹️ [좌측 하단 블랙 존] 크레인 조작 방향키 배치
        // -------------------------------------------------------------------------
        float lx = 60f;          // 왼쪽 벽에서 떨어질 거리
        float ly = sh - 180f;    // 바닥에서 올라올 거리
        bool canMove = coins > 0 && state == State.Aim;
        
        // 💡 [수정] 버튼이 눌려 있는 동안만 상태 변수를 true로 유지합니다.
        uiLeft = GUI.RepeatButton(new Rect(lx, ly + 50f, 60f, 50f), "◀", _btn) && canMove;
        uiRight = GUI.RepeatButton(new Rect(lx + 130f, ly + 50f, 60f, 50f), "▶", _btn) && canMove;
        uiUp = GUI.RepeatButton(new Rect(lx + 65f, ly, 60f, 50f), "▲", _btn) && canMove;
        uiDown = GUI.RepeatButton(new Rect(lx + 65f, ly + 100f, 60f, 50f), "▼", _btn) && canMove;

        // -------------------------------------------------------------------------
        // 3. 🔴 [우측 하단 블랙 존] GET! 및 세팅 컨트롤러 격리 배치
        // -------------------------------------------------------------------------
        float rx = sw - 280f;    // 오른쪽 벽에서 떨어질 거리
        float ry = sh - 250f;    // 본체 높이를 침범하지 않도록 안정적인 우하단 정렬

        // GET! 버튼을 우측에 큼직하게 배치하여 조작 편의성 증대
        if (GUI.Button(new Rect(rx, ry, 240f, 80f), "GET!", _big)) DoGet();

        GUI.Label(new Rect(rx, ry + 95f, 240f, 30f), $"집게 힘  {(int)gripForce}N", _label);
        gripForce = GUI.HorizontalSlider(new Rect(rx, ry + 125f, 240f, 20f), gripForce, 0f, 50f);

        if (GUI.Button(new Rect(rx, ry + 160f, 115f, 44f), "리셋", _btn)) ResetStats();
        if (GUI.Button(new Rect(rx + 125f, ry + 160f, 115f, 44f), "+5 코인", _btn)) InsertCoins(5);

        // -------------------------------------------------------------------------
        // 4. 하단 중앙 시스템 메시지 가이드
        // -------------------------------------------------------------------------
        GUI.Label(new Rect(sw / 2f - 250f, sh - 60f, 500f, 60f), "방향키/WASD: 이동  |  스페이스/GET: 집게 작동  |  우클릭 드래그: 시점 회전", _label);

        // -------------------------------------------------------------------------
        // 5. 전광판 배너 및 효과 연출 (기존 로직 유지)
        // -------------------------------------------------------------------------
        if (coinFlash > 0f)
        {
            GUI.color = new Color(1f, 0.85f, 0.2f, Mathf.Clamp01(coinFlash * 1.5f));
            GUI.Label(new Rect(0f, sh * 0.46f, sw, 70f), "코인 투입!", _banner);
            GUI.color = prev;
        }

        if (coins <= 0 && successFlash <= 0f && state == State.Aim)
        {
            var gr = new Rect(sw / 2f - 280f, sh * 0.30f, 560f, 64f);
            var oc = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.62f);
            GUI.DrawTexture(gr, Texture2D.whiteTexture);   
            float pulse = 0.7f + 0.3f * Mathf.Abs(Mathf.Sin(Time.time * 3f));
            GUI.color = new Color(1f, 0.98f, 0.55f, pulse);
            GUI.Label(gr, "▶  '+5 코인' 버튼을 눌러 시작!", _title);
            GUI.color = oc;
        }

        if (successFlash > 0f)
        {
            GUI.color = new Color(1f, 0.95f, 0.3f, Mathf.Clamp01(successFlash));
            GUI.Label(new Rect(0f, sh * 0.32f, sw, 90f), "★ 성공! ★", _banner);
            GUI.color = prev;
        }
    }

    void InsertCoins(int amount)
    {
        coins += amount;
        coinFlash = 1.2f;
        if (sfxSrc != null) sfxSrc.PlayOneShot(clipCoin);
        if (bgmSrc != null && !bgmSrc.isPlaying) bgmSrc.Play(); 
    }

    void ResetStats()
    {
        coinsUsed = 0; successCount = 0; failCount = 0;
        coins = 0; collected = 0;
        successFlash = 0f;
        StartMode();   
    }
}