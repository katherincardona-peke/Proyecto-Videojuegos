using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Mapa estilo Albion: se abre con M encima del juego (sin cambiar de escena).
//  Pestana ISLA: la isla pintada como mapa ilustrado en rombo (estilo Albion), con niebla que se descubre
//  al caminar, pines con tooltip, leyenda y datos de la zona.
//  Pestana ARCHIPIELAGO: islas por peligro (verde, amarillo, rojo, negro), rutas de tinta, niebla,
//  requisitos como sellos y boton "Trazar ruta". Despues se zarpa desde un Muelle.
// Controles: M / Esc cerrar, Tab cambiar pestana, rueda zoom, arrastrar para mover.
// Lo crea el HUD solo en las escenas Isla y Navegacion.
public class MapaMundo : MonoBehaviour
{
    public enum Pestana { Isla, Mundo }

    public static MapaMundo Actual;
    public static bool Abierto;

    // ---------- configuracion
    const float AnchoVentana = 1640f, AltoVentana = 900f;
    const float AnchoMundo = 1500f, AltoMundo = 760f;      // area del mapa local
    const float AnchoArch = 1060f, AltoArch = 740f;        // area del archipielago
    static readonly Vector2 BaseArch = new Vector2(-250f, 0f);   // corrido a la izquierda: a la derecha va el panel
    const int CeldasNiebla = 48;          // resolucion de la niebla del mapa local
    const float RadioExplorar = 14f;      // metros que descubre Kai a su alrededor

    Font fuente; Sprite cuadro, circulo, aro, triangulo;
    Color dorado, tinta, papel;
    readonly Color cera = new Color(0.62f, 0.1f, 0.1f);

    RectTransform raiz, ventana, vista, contenidoLocal, contenidoMundo, capaIconosLocal, panelInfo;
    Text titulo, ayuda, tabIsla, tabMundo;
    Image fondoTabIsla, fondoTabMundo;
    Pestana pestana = Pestana.Isla;
    int cuadroApertura, cuadroCierre = -1;

    // local
    RawImage imagenIsla, imagenNiebla;
    RenderTexture rtIsla;
    Texture2D texNiebla;
    Rect area; ZonaIsla zonaActual;
    Vector2 tamLocal;
    Image flechaKai;
    readonly Dictionary<MarcadorMinimapa, Image> iconosLocal = new Dictionary<MarcadorMinimapa, Image>();
    static readonly Dictionary<int, bool[]> explorado = new Dictionary<int, bool[]>();
    float proximaExploracion, proximaBusqueda;

    // mundo
    class VisualIsla { public Archipielago.Isla datos; public RectTransform raiz; public Image isla, anillo, brillo; public Text nombre, peligro; public List<Image> niebla = new List<Image>(); public List<Image> ruta = new List<Image>(); public bool conArte; }
    readonly Dictionary<int, VisualIsla> visuales = new Dictionary<int, VisualIsla>();
    RectTransform barco;
    int elegida;
    bool animandoTinta;
    Text pNombre, pPeligro, pTipo, pDesc, pRecompensa, pEstado, pBoton;
    RectTransform pSellos; Image pBotonImg;

    // zoom y arrastre
    float zoom = 1f; Vector2 ultimoMouse; float arrastre; bool arrastrando;

    // ================================================================ api
    public static MapaMundo Crear(RectTransform padre, Font fuente, Color dorado, Color tinta, Color papel)
    {
        var go = new GameObject("MapaMundo", typeof(RectTransform));
        go.transform.SetParent(padre, false);
        var m = go.AddComponent<MapaMundo>();
        m.fuente = fuente; m.dorado = dorado; m.tinta = tinta; m.papel = papel;
        m.Construir();
        return m;
    }

    public static void Abrir(Pestana p) { if (Actual) Actual.AbrirInterno(p); }
    public static void Cerrar() { if (Actual) Actual.CerrarInterno(); }

    void Awake() { Actual = this; }
    void OnDestroy()
    {
        if (Actual == this) { Actual = null; Abierto = false; }
        if (rtIsla) rtIsla.Release();
    }

    void AbrirInterno(Pestana p)
    {
        if (Abierto || Time.frameCount == cuadroCierre) return;   // la misma tecla M no lo reabre al cerrarlo
        if (!GameState.Kai) p = Pestana.Mundo;    // en la navegacion solo hay mapa del mundo
        Abierto = true;
        cuadroApertura = Time.frameCount;
        HUD.Pausado = true; Time.timeScale = 0f;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        raiz.gameObject.SetActive(true);
        Cambiar(p);
    }

    void CerrarInterno()
    {
        if (animandoTinta) return;
        Abierto = false;
        cuadroCierre = Time.frameCount;
        raiz.gameObject.SetActive(false);
        HUD.Pausado = false; Time.timeScale = 1f;
    }

    void Cambiar(Pestana p)
    {
        pestana = p;
        contenidoLocal.gameObject.SetActive(p == Pestana.Isla);
        contenidoMundo.gameObject.SetActive(p == Pestana.Mundo);
        localFijo.gameObject.SetActive(p == Pestana.Isla);
        tooltip.gameObject.SetActive(false);
        panelInfo.parent.gameObject.SetActive(p == Pestana.Mundo);
        fondoTabIsla.color = p == Pestana.Isla ? dorado : new Color(0, 0, 0, 0.35f);
        fondoTabMundo.color = p == Pestana.Mundo ? dorado : new Color(0, 0, 0, 0.35f);
        tabIsla.color = p == Pestana.Isla ? tinta : papel;
        tabMundo.color = p == Pestana.Mundo ? tinta : papel;
        zoom = 1f;
        ContenidoActivo().localScale = Vector3.one; ContenidoActivo().anchoredPosition = Base();

        if (p == Pestana.Isla)
        {
            PrepararLocal();
            titulo.text = Archipielago.Buscar(GameState.IslaActual)?.nombre.ToUpper() ?? "ISLA";
        }
        else
        {
            titulo.text = "ARCHIPIÉLAGO";
            RefrescarMundo();
            if (elegida == 0) elegida = GameState.RutaTrazada ? GameState.IslaDestino : SiguienteDestino();
            MostrarInfo(elegida);
            StartCoroutine(RevelarNuevas());
        }
    }

    RectTransform ContenidoActivo() => pestana == Pestana.Isla ? contenidoLocal : contenidoMundo;
    Vector2 Base() => pestana == Pestana.Isla ? Vector2.zero : BaseArch;

    // ================================================================ cada cuadro
    void Update()
    {
        Explorar();
        if (!Abierto) return;

        if (Time.frameCount != cuadroApertura && !animandoTinta)
        {
            if (Input.GetKeyDown(KeyCode.M) || Input.GetKeyDown(KeyCode.Escape)) { CerrarInterno(); return; }
            if (Input.GetKeyDown(KeyCode.Tab) && GameState.Kai) Cambiar(pestana == Pestana.Isla ? Pestana.Mundo : Pestana.Isla);
        }
        if (!animandoTinta) ZoomYArrastre();   // mientras la tinta anima, el mapa se mueve solo
        if (pestana == Pestana.Isla) RefrescarIconosLocal();
        else AnimarMundo();
    }

    void ZoomYArrastre()
    {
        var c = ContenidoActivo();
        float rueda = Input.mouseScrollDelta.y;
        if (Mathf.Abs(rueda) > 0.01f)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(vista, Input.mousePosition, null, out Vector2 m);
            float nuevo = Mathf.Clamp(zoom * Mathf.Pow(1.15f, rueda), 0.8f, 3f);
            c.anchoredPosition = m - (m - c.anchoredPosition) * (nuevo / zoom);
            zoom = nuevo; c.localScale = Vector3.one * zoom;
        }
        if (Input.GetMouseButtonDown(0)) { ultimoMouse = Input.mousePosition; arrastre = 0f; arrastrando = false; }
        if (Input.GetMouseButton(0))
        {
            Vector2 d = (Vector2)Input.mousePosition - ultimoMouse;
            ultimoMouse = Input.mousePosition;
            arrastre += d.magnitude;
            if (arrastre > 8f) arrastrando = true;
            if (arrastrando) c.anchoredPosition += d / Mathf.Max(0.01f, raiz.lossyScale.x);
        }
        // no dejar que el mapa se pierda de vista
        Vector2 lim = new Vector2(AnchoMundo, AltoMundo) * 0.5f * zoom, b0 = Base();
        c.anchoredPosition = new Vector2(Mathf.Clamp(c.anchoredPosition.x, b0.x - lim.x, b0.x + lim.x), Mathf.Clamp(c.anchoredPosition.y, -lim.y, lim.y));
    }

    // ================================================================ construccion de la ventana
    void Construir()
    {
        cuadro = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 4, 4), new Vector2(.5f, .5f));
        circulo = Circulo(128, 0f); aro = Circulo(128, 0.78f); triangulo = Triangulo(64);

        raiz = GetComponent<RectTransform>();
        Estirar(raiz);
        var velo = Img(raiz, cuadro, new Color(0.02f, 0.03f, 0.05f, 0.78f)); Estirar(velo.rectTransform); velo.raycastTarget = true;

        // marco de madera y papel
        var marco = Img(raiz, cuadro, new Color(0.3f, 0.18f, 0.08f));
        Poner(marco.rectTransform, new Vector2(.5f, .5f), Vector2.zero, new Vector2(AnchoVentana + 36, AltoVentana + 36));
        Adornos.Decorar(marco, new Color(0.3f, 0.18f, 0.08f), Adornos.Oro);   // marco de madera con relieve y remaches
        ventana = Img(marco.rectTransform, Pergamino(), Color.white).rectTransform; Estirar(ventana, 18);

        titulo = Texto(ventana, "", 40, FontStyle.Bold, tinta, TextAnchor.MiddleCenter);
        Poner(titulo.rectTransform, new Vector2(.5f, 1), new Vector2(0, -12), new Vector2(900, 56));
        Pestanas();

        // zona visible con recorte
        vista = new GameObject("Vista", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
        vista.SetParent(ventana, false);
        vista.anchorMin = Vector2.zero; vista.anchorMax = Vector2.one; vista.offsetMin = new Vector2(16, 44); vista.offsetMax = new Vector2(-16, -76);

        contenidoLocal = Contenedor("Local");
        contenidoMundo = Contenedor("Mundo");
        contenidoMundo.sizeDelta = new Vector2(AnchoArch, AltoArch);
        ConstruirLocal();
        ConstruirMundo();
        ConstruirPanel();

        ayuda = Texto(ventana, "M / Esc: cerrar   ·   Tab: cambiar mapa   ·   Rueda: zoom   ·   Arrastrar: mover", 19, FontStyle.Italic, tinta, TextAnchor.MiddleCenter);
        Poner(ayuda.rectTransform, new Vector2(.5f, 0), new Vector2(0, 10), new Vector2(1200, 28));

        raiz.gameObject.SetActive(false);
    }

    void Pestanas()
    {
        fondoTabIsla = Boton(ventana, new Vector2(24, -16), new Vector2(170, 46), () => { if (GameState.Kai) Cambiar(Pestana.Isla); }, out tabIsla, "ISLA");
        fondoTabMundo = Boton(ventana, new Vector2(204, -16), new Vector2(230, 46), () => Cambiar(Pestana.Mundo), out tabMundo, "ARCHIPIÉLAGO");
        Adornos.Biselar(fondoTabIsla); Adornos.Biselar(fondoTabMundo);
        Adornos.Sombra(fondoTabIsla.gameObject, 3f); Adornos.Sombra(fondoTabMundo.gameObject, 3f);
    }

    RectTransform Contenedor(string n)
    {
        var r = new GameObject(n, typeof(RectTransform)).GetComponent<RectTransform>();
        r.SetParent(vista, false);
        Poner(r, new Vector2(.5f, .5f), Vector2.zero, new Vector2(AnchoMundo, AltoMundo));
        return r;
    }

    // ================================================================ pestana ISLA (mapa local estilo Albion)
    // La isla se fotografia desde arriba, se "pinta" por codigo (pasto con arboles, arena, agua con olas,
    // costa entintada) y se muestra como un rombo con marco, letras de brujula, pines y tooltip.
    const float LadoLocal = 490f;          // lado del rombo en pantalla (sin zoom)
    const int ResolucionMapa = 512;        // pixeles de la imagen pintada
    RectTransform diamante, localFijo, tooltip;
    Text ttTitulo, ttDetalle, infoLocal, infoNombre;
    Image anilloKai;
    Sprite pin, sombraPin;
    readonly Dictionary<int, Texture2D> islasPintadas = new Dictionary<int, Texture2D>();
    readonly Dictionary<MarcadorMinimapa, RectTransform> centrosPin = new Dictionary<MarcadorMinimapa, RectTransform>();

    void ConstruirLocal()
    {
        pin = Pin(); sombraPin = Circulo(32, 0f);

        diamante = new GameObject("Rombo", typeof(RectTransform)).GetComponent<RectTransform>();
        diamante.SetParent(contenidoLocal, false);
        Poner(diamante, new Vector2(.5f, .5f), new Vector2(0, 6), new Vector2(LadoLocal, LadoLocal));
        diamante.localRotation = Quaternion.Euler(0, 0, 45f);

        // marco: sombra, madera, oro y tinta
        var sombra = Img(diamante, cuadro, new Color(0, 0, 0, 0.35f));
        Poner(sombra.rectTransform, new Vector2(.5f, .5f), new Vector2(10, -2), Vector2.one * (LadoLocal + 44));
        Img(diamante, cuadro, new Color(0.33f, 0.2f, 0.09f)).rectTransform.sizeDelta = Vector2.one * (LadoLocal + 38);
        Img(diamante, cuadro, dorado).rectTransform.sizeDelta = Vector2.one * (LadoLocal + 22);
        Img(diamante, cuadro, new Color(0.45f, 0.29f, 0.13f)).rectTransform.sizeDelta = Vector2.one * (LadoLocal + 16);
        Img(diamante, cuadro, new Color(0.2f, 0.13f, 0.06f)).rectTransform.sizeDelta = Vector2.one * (LadoLocal + 6);

        imagenIsla = new GameObject("Isla", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        imagenIsla.transform.SetParent(diamante, false);
        imagenIsla.raycastTarget = false;
        imagenNiebla = new GameObject("Niebla", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        imagenNiebla.transform.SetParent(diamante, false);
        imagenNiebla.raycastTarget = false;
        capaIconosLocal = new GameObject("Iconos", typeof(RectTransform)).GetComponent<RectTransform>();
        capaIconosLocal.SetParent(diamante, false);
        foreach (var r in new[] { imagenIsla.rectTransform, imagenNiebla.rectTransform, capaIconosLocal })
            Poner(r, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * LadoLocal);

        // adornos dorados en las esquinas y letras de brujula en el centro de cada lado
        float h = LadoLocal / 2f + 19f;
        foreach (var e in new[] { new Vector2(h, h), new Vector2(-h, h), new Vector2(h, -h), new Vector2(-h, -h) })
        {
            var a = Img(diamante, circulo, dorado); Poner(a.rectTransform, new Vector2(.5f, .5f), e, new Vector2(22, 22));
            var b = Img(a.rectTransform, circulo, new Color(0.33f, 0.2f, 0.09f)); Poner(b.rectTransform, new Vector2(.5f, .5f), Vector2.zero, new Vector2(11, 11));
        }
        float l = LadoLocal / 2f + 48f;
        Letra("N", new Vector2(0, l)); Letra("E", new Vector2(l, 0)); Letra("S", new Vector2(0, -l)); Letra("O", new Vector2(-l, 0));

        // Kai: anillo dorado que late + flecha
        anilloKai = Img(capaIconosLocal, aro, dorado);
        anilloKai.rectTransform.sizeDelta = new Vector2(40, 40);
        flechaKai = Img(capaIconosLocal, triangulo, new Color(1f, 0.85f, 0.3f));
        flechaKai.rectTransform.sizeDelta = new Vector2(30, 30);
        flechaKai.gameObject.AddComponent<Outline>().effectColor = new Color(0.2f, 0.12f, 0.04f);

        // paneles fijos (no se mueven con el zoom): leyenda a la izquierda, datos de la zona a la derecha
        localFijo = new GameObject("LocalFijo", typeof(RectTransform)).GetComponent<RectTransform>();
        localFijo.SetParent(ventana, false); Estirar(localFijo);
        ConstruirLeyenda();
        ConstruirInfoLocal();

        // tooltip al pasar el mouse por un pin
        var tt = Img(raiz, cuadro, new Color(0.13f, 0.08f, 0.04f, 0.94f));
        tooltip = tt.rectTransform; tooltip.pivot = Vector2.zero; tooltip.anchorMin = tooltip.anchorMax = new Vector2(.5f, .5f);
        tooltip.sizeDelta = new Vector2(320, 74);
        Adornos.Decorar(tt, new Color(0.13f, 0.08f, 0.04f, 0.96f), dorado, false);
        ttTitulo = Texto(tooltip, "", 22, FontStyle.Bold, dorado, TextAnchor.UpperLeft);
        Poner(ttTitulo.rectTransform, new Vector2(0, 1), new Vector2(12, -8), new Vector2(296, 30));
        ttDetalle = Texto(tooltip, "", 18, FontStyle.Italic, papel, TextAnchor.UpperLeft);
        Poner(ttDetalle.rectTransform, new Vector2(0, 1), new Vector2(12, -40), new Vector2(296, 28));
        tooltip.gameObject.SetActive(false);
    }

    void Letra(string s, Vector2 pos)
    {
        var disco = Img(diamante, circulo, dorado); Poner(disco.rectTransform, new Vector2(.5f, .5f), pos, new Vector2(46, 46));
        var dentro = Img(disco.rectTransform, circulo, new Color(0.25f, 0.15f, 0.07f)); Poner(dentro.rectTransform, new Vector2(.5f, .5f), Vector2.zero, new Vector2(38, 38));
        var t = Texto(disco.rectTransform, s, 26, FontStyle.Bold, dorado, TextAnchor.MiddleCenter);
        Estirar(t.rectTransform);
        disco.rectTransform.localRotation = Quaternion.Euler(0, 0, -45f);   // la letra queda derecha
    }

    void ConstruirLeyenda()
    {
        var p = Img(localFijo, cuadro, new Color(0.15f, 0.09f, 0.04f, 0.82f));
        Poner(p.rectTransform, new Vector2(0, .5f), new Vector2(36, -10), new Vector2(250, 410));
        Adornos.Decorar(p, new Color(0.15f, 0.09f, 0.04f, 0.92f), dorado);
        var t = Texto(p.rectTransform, "LEYENDA", 24, FontStyle.Bold, dorado, TextAnchor.MiddleCenter);
        Poner(t.rectTransform, new Vector2(.5f, 1), new Vector2(0, -12), new Vector2(230, 34));
        Adornos.Separador(p.rectTransform, new Vector2(.5f, 1), new Vector2(0, -50), 200f, dorado);
        var filas = new (MarcadorMinimapa.Tipo tipo, string nombre)[] {
            (MarcadorMinimapa.Tipo.Enemigo, "Enemigo"), (MarcadorMinimapa.Tipo.Jefe, "Jefe"), (MarcadorMinimapa.Tipo.Cofre, "Cofre"),
            (MarcadorMinimapa.Tipo.Personaje, "Personaje"), (MarcadorMinimapa.Tipo.Objetivo, "Prueba / objetivo"),
            (MarcadorMinimapa.Tipo.Recurso, "Recurso"), (MarcadorMinimapa.Tipo.Puerto, "Muelle") };
        float y = -66f;
        var k = Img(p.rectTransform, triangulo, new Color(1f, 0.85f, 0.3f)); Poner(k.rectTransform, new Vector2(0, 1), new Vector2(26, y + 12), new Vector2(24, 24));
        var kt = Texto(p.rectTransform, "Kai (tú)", 20, FontStyle.Bold, papel, TextAnchor.MiddleLeft); Poner(kt.rectTransform, new Vector2(0, 1), new Vector2(64, y + 14), new Vector2(170, 28));
        foreach (var f in filas)
        {
            y -= 44f;
            var ic = Img(p.rectTransform, pin, Color.white); Poner(ic.rectTransform, new Vector2(0, 1), new Vector2(26, y + 18), new Vector2(26, 34));
            var c = Img(ic.rectTransform, circulo, MarcadorMinimapa.ColorDe(f.tipo)); Poner(c.rectTransform, new Vector2(.5f, 1), new Vector2(0, -3), new Vector2(18, 18));
            var g = Texto(c.rectTransform, Glifo(f.tipo, ""), 13, FontStyle.Bold, new Color(0.15f, 0.08f, 0.03f), TextAnchor.MiddleCenter); Estirar(g.rectTransform);
            var n = Texto(p.rectTransform, f.nombre, 20, FontStyle.Normal, papel, TextAnchor.MiddleLeft); Poner(n.rectTransform, new Vector2(0, 1), new Vector2(64, y + 14), new Vector2(170, 28));
        }
    }

    void ConstruirInfoLocal()
    {
        var p = Img(localFijo, cuadro, new Color(0.15f, 0.09f, 0.04f, 0.82f));
        Poner(p.rectTransform, new Vector2(1, .5f), new Vector2(-36, -10), new Vector2(290, 300));
        Adornos.Decorar(p, new Color(0.15f, 0.09f, 0.04f, 0.92f), dorado);
        Adornos.Separador(p.rectTransform, new Vector2(.5f, 1), new Vector2(0, -76), 230f, dorado);
        infoNombre = Texto(p.rectTransform, "", 24, FontStyle.Bold, dorado, TextAnchor.MiddleCenter);
        Poner(infoNombre.rectTransform, new Vector2(.5f, 1), new Vector2(0, -12), new Vector2(270, 60));
        infoLocal = Texto(p.rectTransform, "", 20, FontStyle.Normal, papel, TextAnchor.UpperLeft);
        Poner(infoLocal.rectTransform, new Vector2(.5f, 1), new Vector2(0, -84), new Vector2(250, 210));
        infoLocal.lineSpacing = 1.25f;
    }

    ZonaIsla BuscarZona(int id)
    {
        foreach (var z in FindObjectsByType<ZonaIsla>(FindObjectsSortMode.None)) if (z.idIsla == id) return z;
        return null;
    }

    // el rombo es cuadrado: se agranda el lado corto de la ZonaIsla (sobra un poco de mar, queda bonito)
    static Rect AreaCuadrada(Rect a)
    {
        float lado = Mathf.Max(a.width, a.height);
        return new Rect(a.center.x - lado / 2f, a.center.y - lado / 2f, lado, lado);
    }

    // El mapa cubre la ZonaIsla y ademas todo lo que tenga marcador (cofres, muelle, enemigos, pruebas)
    // y a Kai, por si alguna parte de la isla (por ejemplo el astillero) quedo fuera del rectangulo amarillo.
    // Se calcula una vez por isla para que la niebla no se descuadre.
    static readonly Dictionary<int, Rect> areasIsla = new Dictionary<int, Rect>();
    Rect AreaLocal()
    {
        if (areasIsla.TryGetValue(zonaActual.idIsla, out var guardada)) return guardada;
        Rect r = zonaActual.Area;
        var otras = new List<ZonaIsla>();
        foreach (var z in FindObjectsByType<ZonaIsla>(FindObjectsSortMode.None)) if (z.idIsla != zonaActual.idIsla) otras.Add(z);
        var puntos = new List<Vector3>();
        if (GameState.Kai) puntos.Add(GameState.Kai.position);
        foreach (var m in MarcadorMinimapa.Todos) puntos.Add(m.transform.position);
        float xMin = r.xMin, yMin = r.yMin, xMax = r.xMax, yMax = r.yMax;
        const float margen = 12f;
        foreach (var p in puntos)
        {
            bool deOtra = false;
            foreach (var z in otras) if (z.Area.Contains(new Vector2(p.x, p.z))) deOtra = true;
            // lo que esta muy lejos de esta isla (otra isla de la misma escena) no cuenta
            float lejos = Mathf.Max(Mathf.Max(r.xMin - p.x, p.x - r.xMax), Mathf.Max(r.yMin - p.z, p.z - r.yMax));
            if (deOtra || lejos > 40f) continue;
            xMin = Mathf.Min(xMin, p.x - margen); xMax = Mathf.Max(xMax, p.x + margen);
            yMin = Mathf.Min(yMin, p.z - margen); yMax = Mathf.Max(yMax, p.z + margen);
        }
        if (xMax - xMin > r.width + 1f || yMax - yMin > r.height + 1f)
            Debug.Log("[Mapa] La ZonaIsla " + zonaActual.idIsla + " no cubria toda la isla; el mapa se amplio solo. Mejor agranda el rectangulo amarillo.");
        var a = AreaCuadrada(new Rect(xMin, yMin, xMax - xMin, yMax - yMin));
        areasIsla[zonaActual.idIsla] = a;
        return a;
    }

    void PrepararLocal()
    {
        zonaActual = BuscarZona(GameState.IslaActual);
        Vector3 c = GameState.Kai ? GameState.Kai.position : Vector3.zero;
        area = zonaActual ? AreaLocal() : AreaCuadrada(new Rect(c.x - 50f, c.z - 50f, 100f, 100f));
        tamLocal = Vector2.one * LadoLocal;

        if (islasPintadas.TryGetValue(GameState.IslaActual, out var hecha) && hecha) { imagenIsla.texture = hecha; imagenIsla.color = Color.white; }
        else StartCoroutine(FotografiarIsla());
        imagenNiebla.texture = Niebla(GameState.IslaActual);
        imagenNiebla.enabled = imagenNiebla.texture != null;   // sin ZonaIsla no hay niebla
        if (!zonaActual) GameState.Avisar("Falta una ZonaIsla en esta isla: el mapa muestra solo 100 m alrededor de Kai.", 3f);

        var isla = Archipielago.Buscar(GameState.IslaActual);
        infoNombre.text = isla != null ? isla.nombre : "Isla";
    }

    // una camara mira la isla desde arriba un instante; luego la foto se pinta con el estilo del mapa
    IEnumerator FotografiarIsla()
    {
        int n = ResolucionMapa * 2;
        if (!rtIsla || rtIsla.width != n)
        {
            if (rtIsla) rtIsla.Release();
            rtIsla = new RenderTexture(n, n, 16);
        }
        imagenIsla.color = new Color(1, 1, 1, 0);   // invisible mientras se pinta
        int id = GameState.IslaActual;

        // esconder personajes para que no salgan en la foto
        var ocultos = new List<Renderer>();
        Ocultar(GameState.Kai, ocultos);
        foreach (var m in MarcadorMinimapa.Todos)
            if (m.tipo == MarcadorMinimapa.Tipo.Enemigo || m.tipo == MarcadorMinimapa.Tipo.Jefe || m.tipo == MarcadorMinimapa.Tipo.Personaje) Ocultar(m.transform, ocultos);

        var go = new GameObject("CamaraMapa");
        var cam = go.AddComponent<Camera>();
        cam.orthographic = true; cam.orthographicSize = area.height / 2f; cam.aspect = 1f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = FondoFoto;
        cam.nearClipPlane = 1f; cam.farClipPlane = 400f;
        go.transform.position = new Vector3(area.center.x, 200f, area.center.y);
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // norte arriba
        cam.targetTexture = rtIsla;
        yield return new WaitForEndOfFrame();

        var previa = RenderTexture.active;
        RenderTexture.active = rtIsla;
        var lectura = new Texture2D(n, n, TextureFormat.RGBA32, false);
        lectura.ReadPixels(new Rect(0, 0, n, n), 0, 0);
        RenderTexture.active = previa;
        Destroy(go);
        foreach (var r in ocultos) if (r) r.enabled = true;

        var foto = lectura.GetPixels(); Destroy(lectura);
        var tex = PintarIsla(foto, n);
        islasPintadas[id] = tex;
        if (GameState.IslaActual == id) { imagenIsla.texture = tex; imagenIsla.color = Color.white; }
    }

    static void Ocultar(Transform t, List<Renderer> lista)
    {
        if (!t) return;
        foreach (var r in t.GetComponentsInChildren<Renderer>()) if (r.enabled) { r.enabled = false; lista.Add(r); }
    }

    // ---------------- "pintor" del mapa: convierte la foto en un mapa ilustrado
    static readonly Color FondoFoto = new Color(0.17f, 0.42f, 0.55f);
    const int Agua = 0, Arena = 1, Pasto = 2, Madera = 3, Piedra = 4, Oscuro = 5;

    Texture2D PintarIsla(Color[] foto, int n)
    {
        int N = ResolucionMapa, k = n / N;
        // 1. reducir a la mitad y clasificar cada pixel por su color
        var src = new Color[N * N]; var clase = new int[N * N]; var brillo = new float[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            Color c = Color.clear;
            for (int dy = 0; dy < k; dy++) for (int dx = 0; dx < k; dx++) c += foto[(y * k + dy) * n + x * k + dx];
            c /= k * k; src[y * N + x] = c;
            Color.RGBToHSV(c, out float h, out float s, out float v);
            brillo[y * N + x] = v;
            int cl = Pasto;                                                     // suelo que no se reconoce: pasto
            if ((h < 0.13f || h > 0.95f) && s > 0.25f && v < 0.6f) cl = Madera;   // cafe oscuro: muelles, casas
            if (h >= 0.06f && h < 0.17f && s > 0.12f && v > 0.5f) cl = Arena;
            if (s < 0.12f) cl = Piedra;
            if (v < 0.1f) cl = Oscuro;
            float dFondo = Mathf.Abs(c.r - FondoFoto.r) + Mathf.Abs(c.g - FondoFoto.g) + Mathf.Abs(c.b - FondoFoto.b);
            if (dFondo < 0.12f || (h >= 0.42f && h < 0.66f && s > 0.2f && v > 0.12f)) cl = Agua;
            clase[y * N + x] = cl;
        }
        // 2. limpiar el ruido (cada pixel toma la clase mayoritaria de sus vecinos)
        clase = Moda(Moda(clase, N), N);
        QuitarMotas(clase, N, 40);   // brillos del agua y objetos sueltos en el mar no son tierra

        // 3. distancias a la costa (para agua poco profunda, espuma y borde de tinta)
        var esTierra = new bool[N * N];
        for (int i = 0; i < esTierra.Length; i++) esTierra[i] = clase[i] != Agua;
        var dTierra = Distancia(esTierra, N, true);
        var dAgua = Distancia(esTierra, N, false);

        // 4. pintar
        Color somero = new Color(0.58f, 0.76f, 0.74f), hondo = new Color(0.33f, 0.54f, 0.62f), espuma = new Color(0.86f, 0.93f, 0.89f);
        Color pasto = new Color(0.56f, 0.67f, 0.33f), arbol = new Color(0.37f, 0.5f, 0.23f), arbolOsc = new Color(0.29f, 0.42f, 0.19f), arbolBorde = new Color(0.24f, 0.34f, 0.15f);
        Color arena = new Color(0.88f, 0.8f, 0.57f), piedra = new Color(0.68f, 0.66f, 0.6f), madera = new Color(0.64f, 0.45f, 0.28f), oscuro = new Color(0.32f, 0.27f, 0.22f);
        Color tintaCosta = new Color(0.24f, 0.28f, 0.25f);
        var px = new Color[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            int i = y * N + x, cl = clase[i];
            float n1 = Mathf.PerlinNoise(x * 0.03f, y * 0.03f);
            Color col;
            if (cl == Agua)
            {
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(dTierra[i] / 45f));
                col = Color.Lerp(somero, hondo, t) * (0.96f + 0.08f * n1);
                if (dTierra[i] >= 3f && dTierra[i] <= 4.5f) col = Color.Lerp(col, espuma, 0.5f);
                float nw = Mathf.PerlinNoise(x * 0.02f + 30f, y * 0.02f);
                bool ola = Mathf.Abs(Mathf.Sin((y + nw * 40f) * 0.32f)) < 0.14f && Mathf.PerlinNoise(x * 0.06f + 9f, y * 0.06f) > 0.68f && dTierra[i] > 10f;
                if (ola) col = Color.Lerp(col, new Color(0.78f, 0.88f, 0.88f), 0.4f);
            }
            else
            {
                switch (cl)
                {
                    case Pasto:
                        float a = Mathf.PerlinNoise(x * 0.05f + 20f, y * 0.05f + 20f) * 0.7f + Mathf.PerlinNoise(x * 0.13f + 5f, y * 0.13f) * 0.3f;
                        col = pasto * (0.93f + 0.12f * n1);
                        if (a > 0.58f) col = arbol;
                        if (a > 0.66f) col = arbolOsc;
                        if (a > 0.58f && a < 0.6f) col = arbolBorde;
                        break;
                    case Arena:
                        col = arena * (0.95f + 0.08f * n1);
                        if (Hash(x, y) < 0.012f) col = new Color(0.68f, 0.58f, 0.38f);
                        break;
                    case Piedra: col = piedra * (0.9f + 0.15f * Mathf.PerlinNoise(x * 0.08f, y * 0.08f)); break;
                    case Oscuro: col = oscuro; break;
                    default: col = madera; break;
                }
                col *= 0.9f + 0.18f * brillo[i];   // conserva un poco el relieve de la foto
                if (dAgua[i] <= 1.5f) col = tintaCosta;           // costa entintada
                else if (dAgua[i] <= 2.5f) col *= 0.82f;          // sombra bajo la tinta
                else if ((x + 1 < N && clase[i + 1] != cl) || (y + 1 < N && clase[i + N] != cl)) col *= 0.7f;   // borde entre zonas
            }
            col.a = 1f;
            px[i] = col;
        }
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels(px); tex.Apply();
        return tex;
    }

    static float Hash(int x, int y) { float h = Mathf.Sin(x * 12.9898f + y * 78.233f) * 43758.547f; return h - Mathf.Floor(h); }

    // manchas de "tierra" mas pequenas que 'minimo' pixeles rodeadas de agua pasan a ser agua
    static void QuitarMotas(int[] c, int N, int minimo)
    {
        var visto = new bool[c.Length]; var pila = new Stack<int>(); var grupo = new List<int>();
        for (int i = 0; i < c.Length; i++)
        {
            if (visto[i] || c[i] == Agua) continue;
            grupo.Clear(); pila.Push(i); visto[i] = true;
            while (pila.Count > 0)
            {
                int j = pila.Pop(); grupo.Add(j);
                int x = j % N, y = j / N;
                if (x > 0 && !visto[j - 1] && c[j - 1] != Agua) { visto[j - 1] = true; pila.Push(j - 1); }
                if (x < N - 1 && !visto[j + 1] && c[j + 1] != Agua) { visto[j + 1] = true; pila.Push(j + 1); }
                if (y > 0 && !visto[j - N] && c[j - N] != Agua) { visto[j - N] = true; pila.Push(j - N); }
                if (y < N - 1 && !visto[j + N] && c[j + N] != Agua) { visto[j + N] = true; pila.Push(j + N); }
            }
            if (grupo.Count < minimo) foreach (int j in grupo) c[j] = Agua;
        }
    }

    static int[] Moda(int[] c, int N)
    {
        var r = (int[])c.Clone(); var cuenta = new int[6];
        for (int y = 1; y < N - 1; y++) for (int x = 1; x < N - 1; x++)
        {
            System.Array.Clear(cuenta, 0, 6);
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) cuenta[c[(y + dy) * N + x + dx]]++;
            int mejor = c[y * N + x], max = 0;
            for (int k = 0; k < 6; k++) if (cuenta[k] > max) { max = cuenta[k]; mejor = k; }
            if (max >= 5) r[y * N + x] = mejor;
        }
        return r;
    }

    // distancia (en pixeles) a la celda mas cercana donde mascara == objetivo
    static float[] Distancia(bool[] mascara, int N, bool objetivo)
    {
        var d = new float[N * N];
        for (int i = 0; i < d.Length; i++) d[i] = mascara[i] == objetivo ? 0f : 9999f;
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            int i = y * N + x; float m = d[i];
            if (x > 0) m = Mathf.Min(m, d[i - 1] + 1f);
            if (y > 0) m = Mathf.Min(m, d[i - N] + 1f);
            d[i] = m;
        }
        for (int y = N - 1; y >= 0; y--) for (int x = N - 1; x >= 0; x--)
        {
            int i = y * N + x; float m = d[i];
            if (x < N - 1) m = Mathf.Min(m, d[i + 1] + 1f);
            if (y < N - 1) m = Mathf.Min(m, d[i + N] + 1f);
            d[i] = m;
        }
        return d;
    }

    Vector2 MundoALocal(Vector3 p)
    {
        float u = (p.x - area.xMin) / area.width - 0.5f, v = (p.z - area.yMin) / area.height - 0.5f;
        return new Vector2(u * tamLocal.x, v * tamLocal.y);
    }

    static string Glifo(MarcadorMinimapa.Tipo t, string letra)
    {
        if (!string.IsNullOrEmpty(letra)) return letra;
        switch (t)
        {
            case MarcadorMinimapa.Tipo.Enemigo: return "!";
            case MarcadorMinimapa.Tipo.Jefe: return "!!";
            case MarcadorMinimapa.Tipo.Cofre: return "$";
            case MarcadorMinimapa.Tipo.Personaje: return "?";
            case MarcadorMinimapa.Tipo.Recurso: return "♣";
            case MarcadorMinimapa.Tipo.Puerto: return "M";
            default: return "★";
        }
    }

    string DetallePin(MarcadorMinimapa m)
    {
        var rival = m.GetComponent<Rival>();
        if (rival) return (rival.esJefe ? "Jefe" : "Enemigo") + "  ·  vida " + rival.Vida + "/" + rival.vidaMaxima;
        var meta = m.GetComponent<Meta>();
        if (meta) return meta.Hecha() ? "Prueba superada" : "Prueba pendiente";
        switch (m.tipo)
        {
            case MarcadorMinimapa.Tipo.Enemigo: return "Enemigo  ·  ¡cuidado con su farol!";
            case MarcadorMinimapa.Tipo.Cofre: return "Cofre  ·  ábrelo con E";
            case MarcadorMinimapa.Tipo.Personaje: return "Habla con él (E)";
            case MarcadorMinimapa.Tipo.Puerto: return "Muelle  ·  zarpa con E";
            case MarcadorMinimapa.Tipo.Recurso: return "Recurso  ·  recógelo";
            default: return "Objetivo";
        }
    }

    float ultimaInfo;
    void RefrescarIconosLocal()
    {
        float latido = 1f + Mathf.Sin(Time.unscaledTime * 5f) * 0.1f;
        if (GameState.Kai)
        {
            Vector2 pk = MundoALocal(GameState.Kai.position);
            flechaKai.rectTransform.anchoredPosition = pk;
            flechaKai.rectTransform.localRotation = Quaternion.Euler(0, 0, -GameState.Kai.eulerAngles.y);
            flechaKai.rectTransform.localScale = Vector3.one * latido / zoom;
            anilloKai.rectTransform.anchoredPosition = pk;
            float onda = Mathf.Repeat(Time.unscaledTime * 0.8f, 1f);
            anilloKai.rectTransform.localScale = Vector3.one * (0.8f + onda * 0.9f) / zoom;
            anilloKai.color = new Color(dorado.r, dorado.g, dorado.b, 1f - onda);
            anilloKai.transform.SetAsLastSibling(); flechaKai.transform.SetAsLastSibling();
        }
        var quitar = new List<MarcadorMinimapa>();
        foreach (var par in iconosLocal) if (!par.Key || !MarcadorMinimapa.Todos.Contains(par.Key)) quitar.Add(par.Key);
        foreach (var k in quitar) { if (iconosLocal[k]) Destroy(iconosLocal[k].gameObject); iconosLocal.Remove(k); centrosPin.Remove(k); }

        // pin bajo el mouse
        Vector2 mouse = Input.mousePosition;
        float radio = 24f * raiz.lossyScale.x, mejor = radio;
        MarcadorMinimapa sobre = null; bool sobreKai = false;

        int enemigos = 0, cofres = 0;
        foreach (var m in MarcadorMinimapa.Todos)
        {
            if (!iconosLocal.TryGetValue(m, out var img)) { img = CrearPin(m); iconosLocal[m] = img; }
            Vector3 p = m.transform.position;
            bool visible = m.Activo && area.Contains(new Vector2(p.x, p.z)) && EstaExplorado(p);
            img.gameObject.SetActive(visible);
            if (m.Activo && (m.tipo == MarcadorMinimapa.Tipo.Enemigo || m.tipo == MarcadorMinimapa.Tipo.Jefe)) { var rv = m.GetComponent<Rival>(); if (!rv || !rv.Muerto) enemigos++; }
            if (m.Activo && m.tipo == MarcadorMinimapa.Tipo.Cofre) cofres++;
            if (!visible) continue;
            img.rectTransform.anchoredPosition = MundoALocal(p);
            img.rectTransform.localScale = Vector3.one * (m == ultimoSobre ? 1.25f : 1f) / zoom;   // no crecen con el zoom
            float d = Vector2.Distance(mouse, RectTransformUtility.WorldToScreenPoint(null, centrosPin[m].position));
            if (d < mejor) { mejor = d; sobre = m; }
        }
        if (GameState.Kai && !sobre && Vector2.Distance(mouse, RectTransformUtility.WorldToScreenPoint(null, flechaKai.rectTransform.position)) < radio) sobreKai = true;
        ultimoSobre = sobre;

        // tooltip
        if (sobre || sobreKai)
        {
            tooltip.gameObject.SetActive(true); tooltip.SetAsLastSibling();
            ttTitulo.text = sobreKai ? "Kai" : Etiqueta(sobre);
            ttDetalle.text = sobreKai ? "Tú estás aquí" : DetallePin(sobre);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(raiz, mouse, null, out Vector2 lp);
            tooltip.anchoredPosition = lp + new Vector2(22, 14);
        }
        else tooltip.gameObject.SetActive(false);

        // datos de la zona (2 veces por segundo)
        if (Time.unscaledTime > ultimaInfo)
        {
            ultimaInfo = Time.unscaledTime + 0.5f;
            var isla = Archipielago.Buscar(GameState.IslaActual);
            string peligro = isla != null ? "<color=#" + ColorUtility.ToHtmlStringRGB(Archipielago.ColorPeligro(isla.peligro)) + ">● " + Archipielago.NombrePeligro(isla.peligro) + "</color>" : "";
            infoLocal.text = "Peligro:  " + peligro +
                             "\nEnemigos:  " + enemigos +
                             "\nCofres:  " + cofres +
                             "\nPruebas:  " + GameState.PruebasHechas + " / 3" +
                             "\nExplorado:  " + PorcentajeExplorado() + "%";
        }
    }
    MarcadorMinimapa ultimoSobre;

    static string Etiqueta(MarcadorMinimapa m)
    {
        if (!string.IsNullOrEmpty(m.etiqueta)) return m.etiqueta;
        var rival = m.GetComponent<Rival>(); if (rival) return rival.nombre;
        return m.gameObject.name;
    }

    // pin estilo Albion: sombra en el suelo, cuerpo de papel, centro de color y simbolo
    Image CrearPin(MarcadorMinimapa m)
    {
        float k = m.tipo == MarcadorMinimapa.Tipo.Jefe ? 1.35f : m.tipo == MarcadorMinimapa.Tipo.Objetivo ? 1.15f : 1f;
        var sombra = Img(capaIconosLocal, sombraPin, new Color(0, 0, 0, 0.45f));
        sombra.rectTransform.sizeDelta = new Vector2(18, 7) * k;
        sombra.rectTransform.localRotation = Quaternion.Euler(0, 0, -45f);   // derecho aunque el rombo este girado
        var cuerpo = Img(sombra.rectTransform, pin, Color.white);
        Poner(cuerpo.rectTransform, new Vector2(.5f, .5f), Vector2.zero, new Vector2(30, 40) * k);
        cuerpo.rectTransform.pivot = new Vector2(.5f, 0f); cuerpo.rectTransform.anchoredPosition = new Vector2(0, -1);
        var centro = Img(cuerpo.rectTransform, circulo, m.ColorIcono());
        Poner(centro.rectTransform, new Vector2(.5f, 1f), new Vector2(0, -4 * k), new Vector2(21, 21) * k);
        centro.rectTransform.pivot = new Vector2(.5f, 1f);
        var g = Texto(centro.rectTransform, Glifo(m.tipo, m.letra), Mathf.RoundToInt(15 * k), FontStyle.Bold, new Color(0.15f, 0.08f, 0.03f), TextAnchor.MiddleCenter);
        Estirar(g.rectTransform);
        centrosPin[m] = centro.rectTransform;
        return sombra;
    }

    int PorcentajeExplorado()
    {
        if (!zonaActual) return 100;
        var c = Celdas(GameState.IslaActual, out _, out _);
        int s = 0; foreach (var b in c) if (b) s++;
        return Mathf.RoundToInt(100f * s / c.Length);
    }

    static bool DentroPin(float x, float y, float enc, int w, int h)
    {
        float R = 29f, cx = w / 2f, cy = h - 2f - R, tip = 2f, r = R - enc;
        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) < r * r) return true;
        float baseY = cy - r * 0.55f;   // el cono se une al circulo un poco por debajo del centro
        if (y < tip + enc * 1.6f || y > baseY) return false;
        float ancho = r * 0.835f * (y - tip - enc * 1.6f) / (baseY - tip - enc * 1.6f);
        return Mathf.Abs(x - cx) < ancho;
    }

    // forma de gota invertida (pin de mapa) con borde de tinta
    static Sprite Pin()
    {
        int w = 64, h = 84; var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var cuerpo = new Color(0.96f, 0.92f, 0.82f); var borde = new Color(0.25f, 0.16f, 0.07f);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int fuera = 0, dentro = 0;
            for (int s = 0; s < 4; s++)
            {
                float sx = x + 0.25f + (s % 2) * 0.5f, sy = y + 0.25f + (s / 2) * 0.5f;
                if (DentroPin(sx, sy, 0f, w, h)) { fuera++; if (DentroPin(sx, sy, 3.5f, w, h)) dentro++; }
            }
            Color c = Color.Lerp(borde, cuerpo, fuera > 0 ? dentro / (float)fuera : 0f);
            c.a = fuera / 4f;
            t.SetPixel(x, y, c);
        }
        t.Apply(); return Sprite.Create(t, new Rect(0, 0, w, h), new Vector2(.5f, 0f));
    }

    // ---------------- niebla de exploracion (se descubre al caminar)
    void Explorar()
    {
        if (!GameState.Kai || Time.unscaledTime < proximaExploracion) return;
        proximaExploracion = Time.unscaledTime + 0.25f;
        if ((!zonaActual || zonaActual.idIsla != GameState.IslaActual) && Time.unscaledTime > proximaBusqueda)
        {
            proximaBusqueda = Time.unscaledTime + 2f;
            zonaActual = BuscarZona(GameState.IslaActual); if (zonaActual) area = AreaLocal();
        }
        if (!zonaActual) return;
        var celdas = Celdas(GameState.IslaActual, out int cw, out int ch);
        Vector3 p = GameState.Kai.position;
        float rx = RadioExplorar / area.width * cw, ry = RadioExplorar / area.height * ch;
        float cx = (p.x - area.xMin) / area.width * cw, cy = (p.z - area.yMin) / area.height * ch;
        bool cambio = false;
        for (int y = Mathf.FloorToInt(cy - ry); y <= Mathf.CeilToInt(cy + ry); y++)
            for (int x = Mathf.FloorToInt(cx - rx); x <= Mathf.CeilToInt(cx + rx); x++)
            {
                if (x < 0 || y < 0 || x >= cw || y >= ch) continue;
                float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry;
                if (dx * dx + dy * dy <= 1f && !celdas[y * cw + x]) { celdas[y * cw + x] = true; cambio = true; }
            }
        if (cambio && Abierto && pestana == Pestana.Isla) imagenNiebla.texture = Niebla(GameState.IslaActual);
    }

    bool[] Celdas(int id, out int cw, out int ch)
    {
        cw = CeldasNiebla; ch = Mathf.Max(8, Mathf.RoundToInt(CeldasNiebla * area.height / Mathf.Max(1f, area.width)));
        if (!explorado.TryGetValue(id, out var c) || c.Length != cw * ch) { c = new bool[cw * ch]; explorado[id] = c; }
        return c;
    }

    bool EstaExplorado(Vector3 p)
    {
        if (!zonaActual) return true;
        var c = Celdas(GameState.IslaActual, out int cw, out int ch);
        int x = Mathf.Clamp(Mathf.FloorToInt((p.x - area.xMin) / area.width * cw), 0, cw - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt((p.z - area.yMin) / area.height * ch), 0, ch - 1);
        return c[y * cw + x];
    }

    // niebla con bordes organicos (como tinta que aun no se ha secado) y papel oscuro con rayado
    const int Detalle = 5;   // pixeles por celda
    Texture2D Niebla(int id)
    {
        if (!zonaActual) return null;
        var c = Celdas(id, out int cw, out int ch);
        int w = cw * Detalle, h = ch * Detalle;
        if (!texNiebla || texNiebla.width != w || texNiebla.height != h)
            texNiebla = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        // suavizar las celdas (dos pasadas de desenfoque) para que los bordes queden redondeados
        var f = new float[cw * ch];
        for (int i = 0; i < f.Length; i++) f[i] = c[i] ? 1f : 0f;
        f = Desenfocar(Desenfocar(f, cw, ch), cw, ch);
        var px = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // valor suave de "explorado" interpolando las celdas vecinas
                float fx = (x + 0.5f) / Detalle - 0.5f, fy = (y + 0.5f) / Detalle - 0.5f;
                int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, cw - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, ch - 1);
                int x1 = Mathf.Min(x0 + 1, cw - 1), y1 = Mathf.Min(y0 + 1, ch - 1);
                float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
                float e = Mathf.Lerp(Mathf.Lerp(f[y0 * cw + x0], f[y0 * cw + x1], tx), Mathf.Lerp(f[y1 * cw + x0], f[y1 * cw + x1], tx), ty);
                float ruido = Mathf.PerlinNoise(x * 0.06f, y * 0.06f) - 0.5f;
                float v = e + ruido * 0.25f;
                float a = 1f - Umbral(0.38f, 0.5f, v);   // ojo: Mathf.SmoothStep NO sirve como umbral
                // papel de la niebla: mas oscuro que el mapa, con rayado diagonal de tinta
                float raya = ((x + y) % 7 == 0) ? 0.82f : 1f;
                float papelRuido = 0.9f + Mathf.PerlinNoise(x * 0.2f + 50f, y * 0.2f) * 0.15f;
                Color col = new Color(0.55f * papelRuido * raya, 0.43f * papelRuido * raya, 0.28f * papelRuido * raya, a);
                // borde de tinta justo donde termina la niebla
                if (v > 0.3f && v < 0.4f) col = new Color(0.3f, 0.19f, 0.09f, 0.95f);
                px[y * w + x] = col;
            }
        texNiebla.SetPixels(px); texNiebla.Apply();
        return texNiebla;
    }

    // 0 antes de a, 1 despues de b, curva suave entre medio
    static float Umbral(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

    static float[] Desenfocar(float[] f, int cw, int ch)
    {
        var r = new float[f.Length];
        for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++)
        {
            float suma = 0; int n = 0;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= cw || yy >= ch) continue;
                suma += f[yy * cw + xx]; n++;
            }
            r[y * cw + x] = suma / n;
        }
        return r;
    }

    // ================================================================ pestana ARCHIPIELAGO
    Vector2 PosMundo(Archipielago.Isla i) => new Vector2((i.posicion.x - 0.5f) * AnchoArch, (i.posicion.y - 0.5f) * AltoArch);

    void ConstruirMundo()
    {
        // Imagen propia (opcional): Assets/Resources/Mapa/archipielago.png
        var fondo = Cargar("Mapa/archipielago");
        var mar = Img(contenidoMundo, fondo ? fondo : Mar(), Color.white); Estirar(mar.rectTransform);
        var brujula = Cargar("Mapa/brujula");
        if (brujula) { var b = Img(contenidoMundo, brujula, Color.white); b.preserveAspect = true; Poner(b.rectTransform, new Vector2(1, 0), new Vector2(-24, 24), new Vector2(150, 150)); }
        else { var b = Img(contenidoMundo, RosaVientos(), new Color(tinta.r, tinta.g, tinta.b, 0.8f)); Poner(b.rectTransform, new Vector2(1, 0), new Vector2(-24, 24), new Vector2(140, 140)); }

        // rutas primero (quedan debajo de las islas)
        foreach (var i in Archipielago.Islas) visuales[i.id] = new VisualIsla { datos = i };
        foreach (var i in Archipielago.Islas)
        {
            if (i.origen == 0) continue;
            var o = Archipielago.Buscar(i.origen); if (o == null) continue;
            Vector2 a = PosMundo(o), b = PosMundo(i);
            Vector2 lado = new Vector2(-(b - a).y, (b - a).x).normalized * Vector2.Distance(a, b) * 0.12f;
            Vector2 c = (a + b) / 2f + lado;
            int n = Mathf.Max(6, Mathf.RoundToInt(Vector2.Distance(a, b) / 24f));
            for (int k = 1; k < n; k++)
            {
                float t = k / (float)n;
                Vector2 p = (1 - t) * (1 - t) * a + 2 * (1 - t) * t * c + t * t * b;
                Vector2 tan = 2 * (1 - t) * (c - a) + 2 * t * (b - c);
                var d = Img(contenidoMundo, cuadro, tinta);
                Poner(d.rectTransform, new Vector2(.5f, .5f), p, new Vector2(13, 4));
                d.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(tan.y, tan.x) * Mathf.Rad2Deg);
                visuales[i.id].ruta.Add(d);
            }
        }
        // islas
        foreach (var i in Archipielago.Islas)
        {
            var v = visuales[i.id];
            v.raiz = new GameObject("Isla_" + i.id, typeof(RectTransform)).GetComponent<RectTransform>();
            v.raiz.SetParent(contenidoMundo, false);
            Poner(v.raiz, new Vector2(.5f, .5f), PosMundo(i), new Vector2(150, 150));
            v.brillo = Img(v.raiz, circulo, new Color(dorado.r, dorado.g, dorado.b, 0f)); v.brillo.rectTransform.sizeDelta = new Vector2(190, 190);
            v.anillo = Img(v.raiz, aro, Archipielago.ColorPeligro(i.peligro)); v.anillo.rectTransform.sizeDelta = new Vector2(150, 150);
            // Imagen propia (opcional): Assets/Resources/Mapa/isla_1.png ... isla_6.png
            var arte = Cargar("Mapa/isla_" + i.id);
            if (arte)
            {
                v.isla = Img(v.raiz, arte, Color.white); v.isla.preserveAspect = true; v.isla.rectTransform.sizeDelta = new Vector2(140, 140);
                v.conArte = true;
            }
            else
            {
                var forma = Mancha(i.id * 7919, 128);
                var playa = Img(v.raiz, forma, new Color(0.9f, 0.82f, 0.6f)); playa.rectTransform.sizeDelta = new Vector2(126, 126);
                v.isla = Img(v.raiz, forma, new Color(0.55f, 0.66f, 0.38f)); v.isla.rectTransform.sizeDelta = new Vector2(108, 108);
            }
            v.nombre = Texto(v.raiz, i.nombre, 22, FontStyle.Bold, tinta, TextAnchor.UpperCenter);
            Poner(v.nombre.rectTransform, new Vector2(.5f, .5f), new Vector2(0, -92), new Vector2(300, 30));
            v.peligro = Texto(v.raiz, "", 16, FontStyle.Bold, Archipielago.ColorPeligro(i.peligro), TextAnchor.UpperCenter);
            Poner(v.peligro.rectTransform, new Vector2(.5f, .5f), new Vector2(0, -116), new Vector2(300, 24));
            // niebla sobre las islas aun no reveladas
            Vector2[] nubes = { new Vector2(0, 8), new Vector2(-40, -10), new Vector2(42, -6), new Vector2(-12, 30), new Vector2(18, -30) };
            float[] tam = { 110, 80, 84, 70, 72 };
            for (int k = 0; k < nubes.Length; k++)
            {
                var nb = Img(v.raiz, circulo, new Color(0.88f, 0.84f, 0.76f, 0.95f));
                Poner(nb.rectTransform, new Vector2(.5f, .5f), nubes[k], new Vector2(tam[k], tam[k] * 0.7f));
                v.niebla.Add(nb);
            }
            // boton invisible para elegir la isla
            var clic = Img(v.raiz, circulo, new Color(0, 0, 0, 0)); clic.raycastTarget = true; clic.rectTransform.sizeDelta = new Vector2(150, 150);
            int id = i.id;
            clic.gameObject.AddComponent<Button>().onClick.AddListener(() => { if (!arrastrando) { elegida = id; MostrarInfo(id); } });
        }
        // ficha del barco
        barco = new GameObject("Barco", typeof(RectTransform)).GetComponent<RectTransform>();
        barco.SetParent(contenidoMundo, false); barco.sizeDelta = new Vector2(60, 60);
        var casco = Img(barco, cuadro, new Color(0.42f, 0.25f, 0.1f)); Poner(casco.rectTransform, new Vector2(.5f, .5f), new Vector2(0, -10), new Vector2(46, 14));
        var vela = Img(barco, triangulo, new Color(0.96f, 0.93f, 0.84f)); Poner(vela.rectTransform, new Vector2(.5f, .5f), new Vector2(0, 12), new Vector2(34, 34));
        vela.gameObject.AddComponent<Outline>().effectColor = tinta; casco.gameObject.AddComponent<Outline>().effectColor = tinta;
        var aqui = Texto(barco, "Estás aquí", 16, FontStyle.BoldAndItalic, tinta, TextAnchor.MiddleCenter);
        Poner(aqui.rectTransform, new Vector2(.5f, .5f), new Vector2(0, 40), new Vector2(160, 24));
    }

    void RefrescarMundo()
    {
        var camino = GameState.RutaTrazada ? Archipielago.Camino(GameState.IslaActual, GameState.IslaDestino) : new List<int>();
        foreach (var v in visuales.Values)
        {
            var i = v.datos;
            bool vista = i.Revelada && GameState.IslaVista(i.id);
            bool revelada = i.Revelada && (vista || animandoTinta);
            foreach (var n in v.niebla) { var c = n.color; c.a = vista ? 0f : 0.95f; n.color = c; }
            v.isla.color = v.conArte ? (vista ? Color.white : new Color(0.5f, 0.47f, 0.42f))
                                     : (vista ? new Color(0.55f, 0.66f, 0.38f) : new Color(0.55f, 0.52f, 0.46f));
            v.anillo.color = vista ? Archipielago.ColorPeligro(i.peligro) : new Color(0.5f, 0.47f, 0.42f, 0.6f);
            v.nombre.text = vista ? i.nombre : "???";
            v.peligro.text = vista ? "Peligro " + Archipielago.NombrePeligro(i.peligro) + (i.tipo == Archipielago.Tipo.Corona ? " · Corona" : "") : "";
            bool enCamino = camino.Contains(i.id) && camino.Contains(i.origen);
            var origen = Archipielago.Buscar(i.origen);
            bool rutaVisible = vista && origen != null && origen.Revelada;
            foreach (var d in v.ruta)
            {
                d.gameObject.SetActive(rutaVisible);
                d.color = enCamino ? dorado : tinta;
                d.rectTransform.sizeDelta = enCamino ? new Vector2(15, 6) : new Vector2(13, 4);
            }
        }
        var actual = Archipielago.Buscar(GameState.IslaActual);
        if (actual != null) barco.anchoredPosition = PosMundo(actual) + new Vector2(70, 50);
    }

    void AnimarMundo()
    {
        barco.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(Time.unscaledTime * 2f) * 6f);
        foreach (var v in visuales.Values)
        {
            float a = v.datos.id == elegida ? 0.35f + Mathf.Sin(Time.unscaledTime * 4f) * 0.15f : 0f;
            var c = v.brillo.color; c.a = Mathf.Lerp(c.a, a, 10f * Time.unscaledDeltaTime); v.brillo.color = c;
            v.raiz.localScale = Vector3.Lerp(v.raiz.localScale, Vector3.one * (v.datos.id == elegida ? 1.08f : 1f), 10f * Time.unscaledDeltaTime);
        }
    }

    // la tinta revela las islas nuevas (solo la primera vez): el mapa se acerca a cada una,
    // se disipa la niebla, la tinta dibuja la ruta y sale un cartel con su nombre.
    // Tambien avisa cuando una isla ya revelada queda ABIERTA (ya cumples lo que pedia, por ejemplo al reclutar a Pico).
    static bool RutaAbiertaAvisada(int id) => RecompensaIsla.Hechas.Contains("ruta:" + id);
    static bool PuedeZarpar(Archipielago.Isla i) => i.id != 1 && i.Revelada && i.Disponible && i.Cumple;

    IEnumerator RevelarNuevas()
    {
        var nuevas = new List<VisualIsla>();
        var abiertas = new List<VisualIsla>();
        foreach (var v in visuales.Values)
        {
            if (v.datos.Revelada && !GameState.IslaVista(v.datos.id)) nuevas.Add(v);
            else if (GameState.IslaVista(v.datos.id) && PuedeZarpar(v.datos) && !RutaAbiertaAvisada(v.datos.id) && v.datos.id > 2) abiertas.Add(v);
        }
        if (nuevas.Count == 0 && abiertas.Count == 0) yield break;
        animandoTinta = true;
        if (nuevas.Count > 0) GameState.Avisar("La Tinta de Marea reacciona a tus hazañas...", 3f);
        foreach (var v in abiertas)
        {
            RecompensaIsla.Hechas.Add("ruta:" + v.datos.id);
            yield return Enfocar(PosMundo(v.datos), 1.6f, 0.7f);
            Sonidos.Tocar("cofre", null, 0.7f, 0f, 0.3f);
            yield return WaitReal(1.2f);   // se queda un momento mostrando la isla
            elegida = v.datos.id; MostrarInfo(elegida);
        }
        foreach (var v in nuevas)
        {
            yield return Enfocar(PosMundo(v.datos), 1.7f, 0.8f);
            foreach (var d in v.ruta) d.gameObject.SetActive(false);
            for (float t = 0; t < 0.7f; t += Time.unscaledDeltaTime)   // se disipa la niebla
            {
                foreach (var n in v.niebla) { var c = n.color; c.a = Mathf.Lerp(0.95f, 0f, t / 0.7f); n.color = c; n.rectTransform.localScale = Vector3.one * (1f + t); }
                yield return null;
            }
            foreach (var d in v.ruta)                                   // la tinta dibuja la ruta
            {
                d.gameObject.SetActive(true); d.color = tinta;
                yield return WaitReal(0.05f);
            }
            GameState.MarcarVista(v.datos.id);
            if (v.datos.id == 2) GameState.RutaRevelada = true;
            RefrescarMundo();
            for (float t = 0; t < 0.4f; t += Time.unscaledDeltaTime)   // la isla da un saltito
            {
                v.raiz.localScale = Vector3.one * (1f + Mathf.Sin(t / 0.4f * Mathf.PI) * 0.25f);
                yield return null;
            }
            elegida = v.datos.id; MostrarInfo(elegida);
            Sonidos.Tocar("nivel", null, 0.8f, 0f, 0.3f);
            if (PuedeZarpar(v.datos)) RecompensaIsla.Hechas.Add("ruta:" + v.datos.id);
            yield return WaitReal(1.5f);   // se queda un momento mostrando la isla nueva
        }
        yield return Enfocar(Vector2.zero, 1f, 0.6f, true);   // vuelve a ver todo el archipielago
        animandoTinta = false;
    }

    // acerca el mapa del mundo a un punto (centrado en la parte visible) con un movimiento suave
    IEnumerator Enfocar(Vector2 punto, float zoomMeta, float dur, bool volver = false)
    {
        var c = contenidoMundo;
        Vector2 p0 = c.anchoredPosition; float z0 = zoom;
        Vector2 p1 = volver ? Base() : Base() - punto * zoomMeta;
        for (float t = 0f; t < dur; t += Time.unscaledDeltaTime)
        {
            float k = t / dur; k = k * k * (3f - 2f * k);
            zoom = Mathf.Lerp(z0, zoomMeta, k);
            c.localScale = Vector3.one * zoom;
            c.anchoredPosition = Vector2.Lerp(p0, p1, k);
            yield return null;
        }
        zoom = zoomMeta; c.localScale = Vector3.one * zoom; c.anchoredPosition = p1;
    }

    // cartel grande en el centro del mapa: titulo, nombre de la isla y una linea de detalle
    RectTransform cartel; CanvasGroup grupoCartel; Text cartelTitulo, cartelNombre, cartelDetalle; Image cartelFranja;
    IEnumerator Cartel(string titulo, string nombre, string detalle, Color color)
    {
        if (!cartel)
        {
            var fondo = Img(ventana, cuadro, new Color(tinta.r, tinta.g, tinta.b, 0.92f));
            cartel = fondo.rectTransform;
            Poner(cartel, new Vector2(.5f, .5f), new Vector2(-120f, 0f), new Vector2(760f, 230f));
            grupoCartel = cartel.gameObject.AddComponent<CanvasGroup>(); grupoCartel.blocksRaycasts = false;
            var borde = cartel.gameObject.AddComponent<Outline>(); borde.effectColor = dorado; borde.effectDistance = new Vector2(4, -4);
            cartelFranja = Img(cartel, cuadro, color); Poner(cartelFranja.rectTransform, new Vector2(.5f, 1f), new Vector2(0, -6), new Vector2(740f, 8f));
            cartelTitulo = Texto(cartel, "", 26, FontStyle.Bold, dorado, TextAnchor.MiddleCenter);
            Poner(cartelTitulo.rectTransform, new Vector2(.5f, 1f), new Vector2(0, -38), new Vector2(720, 36));
            cartelNombre = Texto(cartel, "", 54, FontStyle.Bold, papel, TextAnchor.MiddleCenter);
            Poner(cartelNombre.rectTransform, new Vector2(.5f, 1f), new Vector2(0, -98), new Vector2(740, 70));
            cartelNombre.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0, 0, 0.8f);
            cartelDetalle = Texto(cartel, "", 21, FontStyle.Italic, papel, TextAnchor.UpperCenter);
            Poner(cartelDetalle.rectTransform, new Vector2(.5f, 1f), new Vector2(0, -146), new Vector2(700, 80));
        }
        cartel.SetAsLastSibling();
        cartelTitulo.text = titulo; cartelNombre.text = nombre.ToUpper(); cartelDetalle.text = detalle; cartelFranja.color = color;
        cartel.gameObject.SetActive(true);
        for (float t = 0f; t < 0.3f; t += Time.unscaledDeltaTime)   // entra creciendo
        {
            float k = t / 0.3f;
            grupoCartel.alpha = k;
            cartel.localScale = Vector3.one * Mathf.Lerp(0.6f, 1f, 1f - (1f - k) * (1f - k));
            yield return null;
        }
        grupoCartel.alpha = 1f; cartel.localScale = Vector3.one;
        // se queda 3 s (clic o espacio lo pasa antes)
        float hasta = Time.unscaledTime + 3f;
        while (Time.unscaledTime < hasta && !Input.GetMouseButtonDown(0) && !Input.GetKeyDown(KeyCode.Space)) yield return null;
        for (float t = 0f; t < 0.3f; t += Time.unscaledDeltaTime) { grupoCartel.alpha = 1f - t / 0.3f; yield return null; }
        cartel.gameObject.SetActive(false);
    }

    static IEnumerator WaitReal(float s) { float f = Time.unscaledTime + s; while (Time.unscaledTime < f) yield return null; }

    int SiguienteDestino()
    {
        foreach (var i in Archipielago.Islas) if (!i.EsActual && i.Revelada && i.Cumple && i.Disponible) return i.id;
        return GameState.IslaActual;
    }

    // ================================================================ panel de informacion
    void ConstruirPanel()
    {
        var b = Img(ventana, cuadro, tinta);
        Poner(b.rectTransform, new Vector2(1, .5f), new Vector2(-24, 0), new Vector2(428, 608));
        Adornos.Decorar(b, new Color(0.93f, 0.85f, 0.66f, 0.98f), new Color(0.5f, 0.3f, 0.12f));   // papel con marco de madera
        var p = Img(b.rectTransform, cuadro, new Color(0, 0, 0, 0));
        panelInfo = p.rectTransform;
        Estirar(panelInfo, 4);
        pNombre = Texto(panelInfo, "", 32, FontStyle.Bold, tinta, TextAnchor.UpperLeft); Poner(pNombre.rectTransform, new Vector2(0, 1), new Vector2(24, -18), new Vector2(372, 80));
        pPeligro = Texto(panelInfo, "", 20, FontStyle.Bold, tinta, TextAnchor.UpperLeft); Poner(pPeligro.rectTransform, new Vector2(0, 1), new Vector2(24, -98), new Vector2(372, 26));
        pTipo = Texto(panelInfo, "", 19, FontStyle.Italic, new Color(0.45f, 0.28f, 0.12f), TextAnchor.UpperLeft); Poner(pTipo.rectTransform, new Vector2(0, 1), new Vector2(24, -126), new Vector2(372, 26));
        pDesc = Texto(panelInfo, "", 20, FontStyle.Normal, tinta, TextAnchor.UpperLeft); Poner(pDesc.rectTransform, new Vector2(0, 1), new Vector2(24, -160), new Vector2(372, 100));
        var tr = Texto(panelInfo, "REQUISITOS", 18, FontStyle.Bold, new Color(0.45f, 0.28f, 0.12f), TextAnchor.UpperLeft); Poner(tr.rectTransform, new Vector2(0, 1), new Vector2(24, -268), new Vector2(372, 24));
        pSellos = new GameObject("Sellos", typeof(RectTransform)).GetComponent<RectTransform>(); pSellos.SetParent(panelInfo, false);
        Poner(pSellos, new Vector2(0, 1), new Vector2(24, -298), new Vector2(372, 100));
        pRecompensa = Texto(panelInfo, "", 20, FontStyle.Normal, tinta, TextAnchor.UpperLeft); Poner(pRecompensa.rectTransform, new Vector2(0, 1), new Vector2(24, -404), new Vector2(372, 56));
        pEstado = Texto(panelInfo, "", 19, FontStyle.Italic, cera, TextAnchor.MiddleCenter); Poner(pEstado.rectTransform, new Vector2(.5f, 0), new Vector2(0, 98), new Vector2(400, 50));
        pBotonImg = Boton(panelInfo, Vector2.zero, new Vector2(320, 60), BotonRuta, out pBoton, "TRAZAR RUTA");
        Poner(pBotonImg.rectTransform, new Vector2(.5f, 0), new Vector2(0, 26), new Vector2(320, 60));
        Adornos.VestirBoton(pBotonImg, pBoton);
    }

    void BotonRuta()
    {
        var i = Archipielago.Buscar(elegida); if (i == null) return;
        if (GameState.RutaTrazada && GameState.IslaDestino == i.id) { GameState.RutaTrazada = false; GameState.Avisar("Ruta borrada.", 1.5f); }
        else
        {
            GameState.IslaDestino = i.id; GameState.RutaTrazada = true;
            GameState.Avisar(GameState.Kai ? "Ruta trazada a " + i.nombre + ". Ve al muelle para zarpar." : "Rumbo a " + i.nombre + ".", 3f);
        }
        RefrescarMundo(); MostrarInfo(elegida);
    }

    void MostrarInfo(int id)
    {
        var i = Archipielago.Buscar(id); if (i == null) return;
        foreach (Transform h in pSellos) Destroy(h.gameObject);
        bool vista = i.Revelada && GameState.IslaVista(i.id);
        if (!vista)
        {
            pNombre.text = "???"; pPeligro.text = ""; pTipo.text = "Velada por la niebla";
            pDesc.text = "La Tinta de Marea aún no muestra esta tierra. Supera retos para ganar fragmentos de mapa.";
            pRecompensa.text = "";
            Sello("Fragmentos " + i.fragmentos, GameState.Fragmentos >= i.fragmentos, 0);
            Estado("Faltan fragmentos de mapa", false, null);
            return;
        }
        pNombre.text = i.nombre;
        pPeligro.text = "Peligro: <color=#" + ColorUtility.ToHtmlStringRGB(Archipielago.ColorPeligro(i.peligro)) + ">● " + Archipielago.NombrePeligro(i.peligro) + "</color>";
        pTipo.text = i.tipo == Archipielago.Tipo.Libre ? "Isla libre · combate abierto" : "Isla de la Corona · solo sigilo, de noche";
        pDesc.text = i.descripcion;
        pRecompensa.text = "<b>Recompensa:</b> " + i.recompensa;
        int k = 0;
        if (i.agi > 0) Sello("Agilidad " + i.agi, GameState.Agilidad >= i.agi, k++);
        if (i.com > 0) Sello("Combate " + i.com, GameState.Combate >= i.com, k++);
        if (i.sig > 0) Sello("Sigilo " + i.sig, GameState.Sigilo >= i.sig, k++);
        if (i.nav > 0) Sello("Navegación " + i.nav, GameState.Navegacion >= i.nav, k++);
        if (k == 0) Sello("Sin requisitos", true, 0);

        if (i.EsActual) Estado("Estás aquí", true, null);
        else if (!i.Cumple) Estado("Tus destrezas aún no bastan", false, null);
        else if (!i.Disponible) Estado("Próximamente", false, null);
        else if (GameState.RutaTrazada && GameState.IslaDestino == i.id)
            Estado(GameState.Kai ? "Ruta trazada · ve al muelle para zarpar" : "Rumbo fijado", true, "BORRAR RUTA");
        else Estado("", true, "TRAZAR RUTA");
    }

    void Estado(string texto, bool ok, string boton)
    {
        pEstado.text = texto; pEstado.color = ok ? new Color(0.25f, 0.45f, 0.15f) : cera;
        pBotonImg.gameObject.SetActive(boton != null);
        if (boton != null) pBoton.text = boton;
    }

    void Sello(string texto, bool ok, int k)
    {
        int col = k % 2, fila = k / 2;
        var s = Img(pSellos, circulo, ok ? dorado : cera);
        Poner(s.rectTransform, new Vector2(0, 1), new Vector2(col * 186, -fila * 50), new Vector2(40, 40));
        var m = Texto(s.rectTransform, ok ? "●" : "!", 22, FontStyle.Bold, ok ? tinta : new Color(1f, 0.85f, 0.7f), TextAnchor.MiddleCenter); Estirar(m.rectTransform);
        var t = Texto(pSellos, texto, 19, ok ? FontStyle.Bold : FontStyle.Normal, tinta, TextAnchor.MiddleLeft);
        Poner(t.rectTransform, new Vector2(0, 1), new Vector2(col * 186 + 48, -fila * 50 - 4), new Vector2(136, 32));
    }

    // ================================================================ utilidades de UI
    Image Img(RectTransform padre, Sprite sp, Color col)
    {
        var i = new GameObject("Img", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        i.transform.SetParent(padre, false); i.sprite = sp; i.color = col; i.raycastTarget = false;
        var r = i.rectTransform; r.anchorMin = r.anchorMax = r.pivot = new Vector2(.5f, .5f);
        return i;
    }

    Text Texto(RectTransform padre, string s, int tam, FontStyle st, Color col, TextAnchor al)
    {
        var t = new GameObject("Texto", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
        t.transform.SetParent(padre, false);
        t.font = fuente; t.text = s; t.fontSize = tam; t.fontStyle = st; t.color = col; t.alignment = al;
        t.raycastTarget = false; t.supportRichText = true;
        t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    Image Boton(RectTransform padre, Vector2 pos, Vector2 tam, UnityEngine.Events.UnityAction accion, out Text texto, string s)
    {
        var i = Img(padre, cuadro, tinta); i.raycastTarget = true;
        Poner(i.rectTransform, new Vector2(0, 1), pos, tam);
        i.gameObject.AddComponent<Button>().onClick.AddListener(accion);
        texto = Texto(i.rectTransform, s, 22, FontStyle.Bold, papel, TextAnchor.MiddleCenter); Estirar(texto.rectTransform);
        return i;
    }

    static void Poner(RectTransform r, Vector2 ancla, Vector2 pos, Vector2 tam) { r.anchorMin = r.anchorMax = r.pivot = ancla; r.anchoredPosition = pos; r.sizeDelta = tam; }
    static void Estirar(RectTransform r, float m = 0) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = new Vector2(m, m); r.offsetMax = new Vector2(-m, -m); }

    // ================================================================ texturas generadas por codigo
    static Sprite Circulo(int n, float hueco)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false); float r = n / 2f;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(r, r));
            float a = Mathf.Clamp01(r - d);
            if (hueco > 0) a *= Mathf.Clamp01(d - r * hueco);
            t.SetPixel(x, y, new Color(1, 1, 1, a));
        }
        t.Apply(); return Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(.5f, .5f));
    }

    static Sprite Triangulo(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float u = (x + .5f) / n - .5f, v = (y + .5f) / n;
            t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Abs(u) < (1f - v) * 0.5f && v > 0.08f + Mathf.Abs(u) * 0.6f ? 1 : 0));
        }
        t.Apply(); return Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(.5f, .5f));
    }

    // silueta irregular de isla (cada isla tiene su propia forma)
    static Sprite Mancha(int semilla, int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        float r0 = n * 0.42f, s = semilla * 0.013f;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            Vector2 p = new Vector2(x + .5f - n / 2f, y + .5f - n / 2f);
            float ang = Mathf.Atan2(p.y, p.x);
            float r = r0 * (0.78f + 0.22f * Mathf.PerlinNoise(Mathf.Cos(ang) * 1.6f + s, Mathf.Sin(ang) * 1.6f + s * 1.7f)
                                  + 0.08f * Mathf.Sin(ang * 3f + s * 5f));
            t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01((r - p.magnitude) * 0.8f)));
        }
        t.Apply(); return Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(.5f, .5f));
    }

    // papel del marco
    static Sprite Pergamino()
    {
        int w = 256, h = 144; var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float n = Mathf.PerlinNoise(x * 0.04f, y * 0.04f) * 0.6f + Mathf.PerlinNoise(x * 0.15f, y * 0.15f) * 0.4f;
            float bx = Mathf.Min(x, w - 1 - x) / (float)w, by = Mathf.Min(y, h - 1 - y) / (float)h;
            float borde = Mathf.Clamp01(Mathf.Min(bx, by) * 9f);
            float k = Mathf.Lerp(0.72f, 1f, borde) * Mathf.Lerp(0.9f, 1.03f, n);
            t.SetPixel(x, y, new Color(0.89f * k, 0.79f * k, 0.6f * k, 1));
        }
        t.Apply(); return Sprite.Create(t, new Rect(0, 0, w, h), new Vector2(.5f, .5f));
    }

    // mar de carta marina: papel azulado, manchas suaves y pocas olitas sueltas
    static Sprite Mar()
    {
        int w = 640, h = 448; var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[w * h];
        Color claro = new Color(0.78f, 0.82f, 0.74f), oscuro = new Color(0.62f, 0.72f, 0.70f), ola = new Color(0.42f, 0.52f, 0.52f);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float n = Mathf.PerlinNoise(x * 0.008f + 3f, y * 0.008f) * 0.7f + Mathf.PerlinNoise(x * 0.05f, y * 0.05f) * 0.3f;
            float bx = Mathf.Min(x, w - 1 - x) / (float)w, by = Mathf.Min(y, h - 1 - y) / (float)h;
            float borde = Mathf.Clamp01(Mathf.Min(bx, by) * 7f);   // bordes mas oscuros, como papel viejo
            px[y * w + x] = Color.Lerp(oscuro * 0.85f, Color.Lerp(oscuro, claro, n), borde);
        }
        // olitas sueltas (pocas y pequenas)
        var r = new System.Random(7);
        for (int k = 0; k < 140; k++)
        {
            int cx = r.Next(10, w - 30), cy = r.Next(10, h - 10), largo = r.Next(10, 18);
            for (int i = 0; i < largo; i++)
            {
                int yy = cy + Mathf.RoundToInt(Mathf.Sin(i / (float)largo * Mathf.PI * 2f) * 1.6f);
                int idx = yy * w + cx + i;
                if (idx >= 0 && idx < px.Length) px[idx] = Color.Lerp(px[idx], ola, 0.55f);
            }
        }
        t.SetPixels(px); t.Apply();
        return Sprite.Create(t, new Rect(0, 0, w, h), new Vector2(.5f, .5f));
    }

    // rosa de los vientos dibujada en tinta
    static Sprite RosaVientos()
    {
        int n = 160; var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        float c = n / 2f;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            Vector2 p = new Vector2(x + .5f - c, y + .5f - c);
            float d = p.magnitude / c, ang = Mathf.Atan2(p.y, p.x);
            float puntas = Mathf.Pow(Mathf.Abs(Mathf.Cos(ang * 2f)), 8f) * 0.85f + Mathf.Pow(Mathf.Abs(Mathf.Cos(ang * 2f + Mathf.PI / 2f)), 8f) * 0.5f;
            bool estrella = d < 0.12f + puntas * 0.78f;
            bool anillo = Mathf.Abs(d - 0.62f) < 0.025f;
            t.SetPixel(x, y, new Color(1, 1, 1, estrella || anillo ? 1f : 0f));
        }
        t.Apply(); return Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(.5f, .5f));
    }

    static Sprite Cargar(string ruta)
    {
        var tex = Resources.Load<Texture2D>(ruta);
        return tex ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(.5f, .5f)) : null;
    }
}
