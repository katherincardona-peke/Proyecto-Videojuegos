using UnityEngine;
using UnityEngine.UI;

// Modo para grabar el teaser. No hay que ponerlo en ningun objeto: se crea solo al dar Play.
//   F7  ocultar / mostrar el HUD (para tomas limpias)
//   F8  camara: normal -> orbita lenta alrededor de Kai (o del barco) -> travelling bajo de lado -> normal
//       (en orbita y travelling: rueda del mouse = acercar / alejar, Q/E con Shift = girar mas rapido)
//   F9  camara lenta (40%)
//   F6  franjas de cine: apagadas -> finas -> medias -> apagadas (cada pulsacion cambia)  [F10 queda libre: en Unity Recorder inicia/detiene la grabacion]
// Graba la pantalla con OBS (o Unity Recorder) mientras usas estas teclas.
public class ModoTeaser : MonoBehaviour
{
    enum Camara { Normal, Orbita, Travelling }

    static ModoTeaser instancia;
    Camara modo = Camara.Normal, modoAntes = Camara.Normal;
    bool hudAntes;
    bool hudOculto, lenta;
    int nivelFranjas;                                   // 0 apagadas, 1 finas, 2 medias
    static readonly float[] AltoFranjas = { 0f, 45f, 80f };   // pixeles arriba y abajo (en 1080)
    float angulo, distancia = 8f, alto = 2.6f;
    Vector3 posSuave; bool hayPos;
    GameObject capaFranjas;
    RectTransform franjaArriba, franjaAbajo;
    float altoFranja;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Crear()
    {
        if (instancia) return;
        var go = new GameObject("ModoTeaser");
        DontDestroyOnLoad(go);
        instancia = go.AddComponent<ModoTeaser>();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F7)) { hudOculto = !hudOculto; Debug.Log("[Teaser] HUD " + (hudOculto ? "oculto" : "visible")); }
        if (Input.GetKeyDown(KeyCode.F8))
        {
            modo = (Camara)(((int)modo + 1) % 3);
            hayPos = false;
            var cam = Camera.main;
            if (cam && Objetivo()) angulo = Mathf.Atan2(cam.transform.position.x - Objetivo().position.x, cam.transform.position.z - Objetivo().position.z) * Mathf.Rad2Deg;
            Debug.Log("[Teaser] Camara: " + modo);
        }
        if (Input.GetKeyDown(KeyCode.F9)) { lenta = !lenta; Debug.Log("[Teaser] Camara lenta " + (lenta ? "SI" : "NO")); }
        if (Input.GetKeyDown(KeyCode.F6)) { nivelFranjas = (nivelFranjas + 1) % AltoFranjas.Length; Debug.Log("[Teaser] Franjas de cine: " + (nivelFranjas == 0 ? "apagadas" : nivelFranjas == 1 ? "finas" : "medias")); }

        // HUD: se oculta el lienzo del juego (el fundido y los menus siguen funcionando)
        if (hudOculto || hudAntes)
            foreach (var c in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (c.name == "UI_Juego") c.enabled = !hudOculto || HUD.Pausado;
        hudAntes = hudOculto;

        // camara lenta: no pelea con la pausa ni con los mapas
        if (!HUD.Pausado && !MapaMundo.Abierto)
        {
            float meta = lenta ? 0.4f : 1f;
            if (Mathf.Abs(Time.timeScale - meta) > 0.01f && Time.timeScale > 0.2f) Time.timeScale = meta;
        }

        // la camara del juego se apaga mientras uso la mia
        var gta = CamaraGTA.Actual;
        if (gta && modo != modoAntes) gta.enabled = modo == Camara.Normal;
        modoAntes = modo;
        if (modo != Camara.Normal)
        {
            distancia = Mathf.Clamp(distancia - Input.mouseScrollDelta.y * 0.8f, 2.5f, 30f);
        }
        Franjas();
    }

    Transform Objetivo()
    {
        var b = BarcoController.Actual;
        if (b && b.Manejando) return b.transform;
        return GameState.Kai;
    }

    void LateUpdate()
    {
        if (modo == Camara.Normal) return;
        var cam = Camera.main; var t = Objetivo();
        if (!cam || !t) return;
        bool barco = t != GameState.Kai;
        float dt = Time.unscaledDeltaTime;
        Vector3 centro = t.position + Vector3.up * (barco ? 2f : 0.6f);
        Vector3 deseada;
        if (modo == Camara.Orbita)
        {
            float vel = Input.GetKey(KeyCode.LeftShift) ? 40f : 12f;
            if (Input.GetKey(KeyCode.LeftShift) && Input.GetKey(KeyCode.Q)) angulo -= vel * dt;
            else angulo += vel * dt;
            float d = barco ? distancia * 2f : distancia;
            deseada = centro + Quaternion.Euler(0f, angulo, 0f) * new Vector3(0f, 0f, d) + Vector3.up * (barco ? alto * 2f : alto);
        }
        else
        {
            // travelling: de lado, bajito, un poco por delante de hacia donde va
            Vector3 lado = t.right;
            deseada = centro + lado * (barco ? distancia * 1.6f : distancia * 0.7f) + t.forward * 1.5f + Vector3.up * (barco ? 1.5f : 0.1f);
        }
        if (!hayPos) { posSuave = cam.transform.position; hayPos = true; }
        posSuave = Vector3.Lerp(posSuave, deseada, 1f - Mathf.Exp(-3f * dt));
        cam.transform.position = posSuave;
        Vector3 mirar = modo == Camara.Travelling ? centro + t.forward * 2f : centro;
        cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, Quaternion.LookRotation(mirar - posSuave), 1f - Mathf.Exp(-6f * dt));
    }

    // franjas negras arriba y abajo (entran y salen suave)
    void Franjas()
    {
        if (!capaFranjas)
        {
            capaFranjas = new GameObject("FranjasCine", typeof(Canvas), typeof(CanvasScaler));
            capaFranjas.transform.SetParent(transform, false);
            var cv = capaFranjas.GetComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = 500;
            var cs = capaFranjas.GetComponent<CanvasScaler>(); cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; cs.referenceResolution = new Vector2(1920, 1080);
            franjaArriba = Franja("Arriba", new Vector2(0, 1));
            franjaAbajo = Franja("Abajo", new Vector2(0, 0));
        }
        altoFranja = Mathf.MoveTowards(altoFranja, AltoFranjas[nivelFranjas], Time.unscaledDeltaTime * 300f);
        franjaArriba.sizeDelta = new Vector2(0, altoFranja);
        franjaAbajo.sizeDelta = new Vector2(0, altoFranja);
    }

    RectTransform Franja(string n, Vector2 pivote)
    {
        var go = new GameObject(n, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(capaFranjas.transform, false);
        go.GetComponent<Image>().color = Color.black;
        go.GetComponent<Image>().raycastTarget = false;
        var r = go.GetComponent<RectTransform>();
        r.anchorMin = new Vector2(0, pivote.y); r.anchorMax = new Vector2(1, pivote.y); r.pivot = new Vector2(0.5f, pivote.y);
        r.sizeDelta = Vector2.zero;
        return r;
    }
}
