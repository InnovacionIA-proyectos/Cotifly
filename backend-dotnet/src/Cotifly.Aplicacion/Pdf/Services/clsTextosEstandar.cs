using System.Collections.Frozen;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Textos estándar del PDF portados literalmente de <c>armar_cotizacion.py</c> (<see cref="TarifaUnica"/>) y
/// <c>armar_multi.py</c> (<see cref="MultiSeccion"/>), con el pie (<c>FOOTER_DIRS</c>), la nota de decretos
/// (<c>NOTA_DECRETO_STD</c>), la frase final y las imágenes y leyendas por línea. El payload puede reemplazar los de
/// cada cotización (<c>textos</c>). Las marcas HTML del Python (<c>&lt;b&gt;</c>, <c>&lt;br&gt;</c>, <c>&lt;a&gt;</c>) pasan
/// a estructura (<see cref="SedesPie"/>, <see cref="EnlaceWeb"/>).
/// </summary>
public static class clsTextosEstandar
{
    /// <summary>Juego de textos editables de una variante.</summary>
    public sealed record clsJuegoTextos(
        IReadOnlyList<string> Intro,
        IReadOnlyList<string> Incluye,
        IReadOnlyList<string> NoIncluye,
        string NotaGeneral,
        string Seguro,
        string StandBy,
        string Vigencia,
        string Facturacion);

    public const string TituloTarifa = "NUESTRA TARIFA PARA SU SOLICITUD ES:";
    public const string TituloTablaPorDefecto = "COTIZACIÓN — BSSL";
    public const string PrefijoOrigen = "ORIGEN Y/O LUGAR DE CARGUE: ";
    public const string TituloDetalleAdicional = "Detalle adicional del servicio";
    public const string TituloIncluye = "Estas tarifas incluyen";
    public const string TituloNoIncluye = "Estas tarifas no incluyen";
    public const string TituloFacturacion = "Facturación y forma de pago";
    public const string TituloSeguro = "Seguro";
    public const string TituloDiasLibres = "Días libres";
    public const string TituloStandByUnica = "Horas logísticas adicionales";
    public const string TituloStandByMulti = "Stand by";
    public const string TituloVigencia = "Vigencia";
    public const string Despedida = "Cordialmente,";
    public const string EtiquetaAsunto = "Asunto:";
    public const string EtiquetaNota = "Nota:";
    public const string EtiquetaTiemposLogisticos = "Tiempos logísticos:";
    public const string EtiquetaCelular = "Cel";

    /// <summary>Encabezados de columna de la tabla de tarifa (tarifa única).</summary>
    public static readonly IReadOnlyList<string> ColumnasUnica = ["Destino", "Tipo de vehículo y peso", "Tarifa", "Nota"];

    /// <summary>Encabezados de columna de la tabla de tarifa (multi-sección).</summary>
    public static readonly IReadOnlyList<string> ColumnasMulti = ["Destino / Sitio entrega", "Tipo de vehículo y peso", "Tarifa", "Nota"];

    /// <summary>Franja web del pie (tarifa única).</summary>
    public const string Web = "www.boterosoto.com.co";

    /// <summary>Destino del enlace de la frase final.</summary>
    public const string EnlaceWeb = "https://www.boterosoto.com.co";

    /// <summary>Primera línea del pie (<c>FOOTER_DIRS</c>).</summary>
    public const string PieEmpresa = "Eduardo Botero Soto S.A. Nit 890.901.321-5";

    /// <summary>Segunda línea del pie: sede en negrita y dirección, separadas por <c> · </c> (<c>FOOTER_DIRS</c>).</summary>
    public static readonly IReadOnlyList<(string Sede, string Direccion)> SedesPie =
    [
        ("Principal:", "Carrera 42 N° 75 – 63 Autopista Sur, Itagüí"),
        ("Bogotá:", "Calle 18 N° 69 – 69"),
        ("Cali:", "Edificio Cencar bloque A4 of. 105/106 Autopista Cali – Yumbo"),
    ];

    /// <summary>
    /// Nota estándar de decretos (<c>NOTA_DECRETO_STD</c>). El Python advierte que el texto oficial del Decreto 16075 está
    /// por confirmar con el área jurídica: este es el texto de referencia.
    /// </summary>
    public const string NotaDecreto =
        "Los tiempos logísticos se rigen por el Decreto 1017 del 21 de septiembre de 2025 "
        + "(RNDC/SICETAC), que determina los valores de los fletes y puede variar mensualmente. "
        + "Asimismo, el Decreto 16075 regula el manejo y la devolución de los contenedores vacíos.";

    /// <summary>Frase de cierre (<c>FRASE_FINAL_STD</c>); el enlace va en la línea siguiente.</summary>
    public const string FraseFinal = "Para conocer más de nuestra organización, ingresa a:";

    private const string NotaGeneralComun =
        "Las tarifas cotizadas no incluyen servicios de cargue ni descargue. Se basan "
        + "en tiempos logísticos de máximo 4 horas para cada labor. En caso de requerir "
        + "un vehículo tipo furgón, el valor se ajustará según lo estipulado por el "
        + "SICETAC. Esta oferta permanecerá vigente siempre que las condiciones del "
        + "mercado de fletes lo permitan. La Superintendencia de Puertos y Transporte, a "
        + "través del RNDC y el SICETAC, determina los valores de los fletes, los cuales "
        + "pueden variar mensualmente, conforme al Decreto 1017 del 21 de septiembre de "
        + "2025. Ante cualquier condición adicional que incida en la operación, las "
        + "tarifas serán revisadas y notificadas oportunamente.";

    private const string VigenciaComun =
        "Esta oferta se mantendrá vigente siempre y cuando las condiciones de fletes "
        + "en el mercado así lo permitan. Si se presentan condiciones que inciden en las "
        + "operaciones, las tarifas serán revisadas y notificadas al cliente.";

    private const string FacturacionComun = "Después de elaborado el estudio de crédito se indicarán los términos.";

    private static readonly string[] _incluyeComun =
    [
        "Flete origen – destino en vehículo",
        "Seguimiento a la carga",
        "Candado satelital",
        "Seguro de la mercancía por un valor máximo de $100.000.000 por vehículo",
    ];

    private static readonly string[] _noIncluyeComun =
    [
        "Acompañamiento vehicular",
        "Otros costos ajenos a la operación",
        "Cargues y descargues",
    ];

    /// <summary>Textos de <c>armar_cotizacion.py</c> (<c>TEXTOS</c>).</summary>
    public static readonly clsJuegoTextos TarifaUnica = new(
        Intro:
        [
            "Para Botero Soto Soluciones Logísticas es muy valioso su tiempo y el "
            + "permitirnos dar a conocer nuestro portafolio de servicios y todos los "
            + "recursos con los que contamos para soportar las diferentes operaciones "
            + "logísticas a nivel internacional y en Colombia, en las más de 21 ciudades "
            + "donde tenemos presencia.",
            "Botero Soto es una Compañía orientada a satisfacer las necesidades de "
            + "nuestros clientes, pero sobre todo, a generar confianza a través de nuestra "
            + "razón de ser.",
            "79 años en el mercado nos permiten actuar con ética, responsabilidad y "
            + "conocimiento en lo que hacemos.",
        ],
        Incluye: _incluyeComun,
        NoIncluye: _noIncluyeComun,
        NotaGeneral: NotaGeneralComun,
        Seguro:
            "EDUARDO BOTERO SOTO S.A. cuenta con una póliza de responsabilidad contractual que "
            + "ampara las obligaciones derivadas del contrato de Transporte, conforme a lo "
            + "establecido en el Código de Comercio. De igual manera, el cliente se compromete a "
            + "contar con una póliza de mercancías que cubra los riesgos y pérdidas de la carga "
            + "objeto de la operación de transporte. En caso de presentarse algún siniestro "
            + "durante el traslado de la mercancía, el cliente realizará la reclamación "
            + "directamente ante su compañía aseguradora, y EDUARDO BOTERO SOTO S.A. atenderá el "
            + "proceso correspondiente mediante acción de subrogación con dicha aseguradora. "
            + "Asimismo, EDUARDO BOTERO SOTO S.A. se compromete a suministrar toda la información "
            + "que se encuentre bajo su control y que sea requerida por el cliente para la "
            + "respectiva reclamación ante la aseguradora. En caso de que el cliente NO cuente con "
            + "una compañía aseguradora, EDUARDO BOTERO SOTO S.A. podrá ofrecer una póliza de "
            + "transporte como usuario permanente para esta operación. Para ello, se tomará una "
            + "póliza especial contratada por EDUARDO BOTERO SOTO S.A., con beneficio a nombre del "
            + "cliente, con una tarifa del 0,05% sobre el valor comercial del viaje y sin "
            + "deducible en reclamaciones por avería particular, saqueo o pérdida total. Esta "
            + "condición aplicará siempre y cuando se mantenga un volumen mínimo de cinco (5) "
            + "viajes mensuales, los cuales podrán corresponder a despachos nacionales.",
        StandBy:
            "Se consideran Horas Logísticas Adicionales los tiempos adicionales a los tiempos "
            + "libres, por causas imputables al cliente en proceso de Cargue, Descargue, "
            + "aduanales, documentales y siempre que excedan las 8 horas hábiles. Para los casos "
            + "descritos el costo de las Horas Logísticas Adicionales estará sujeto a las normas "
            + "vigentes.",
        Vigencia: VigenciaComun,
        Facturacion: FacturacionComun);

    /// <summary>Textos de <c>armar_multi.py</c> (<c>TEXTOS</c>): otra introducción (con viñetas), otro seguro y otro stand by.</summary>
    public static readonly clsJuegoTextos MultiSeccion = new(
        Intro:
        [
            "En Botero Soto Soluciones Logísticas agradecemos la oportunidad de presentar "
            + "nuestra propuesta de servicios. Entendemos la dinámica empresarial actual y "
            + "asumimos con total responsabilidad la confianza depositada en nosotros para "
            + "optimizar sus operaciones. Con 79 años de trayectoria, nos hemos consolidado "
            + "como el aliado estratégico que transforma la cadena de suministro, a través de "
            + "la seguridad, el cumplimiento y la mejora continua y alta eficiencia en cada uno "
            + "de los procesos logísticos. Respaldamos nuestra oferta integral, a través de:",
            "· Cobertura Integral: Presencia estratégica en más de 21 ciudades claves del País.",
            "· Capacidad Global: Experiencia comprobada en la gestión eficiente de operaciones "
            + "logísticas y de transporte, tanto a nivel nacional como internacional.",
            "· Eficiencia Operativa: Soluciones diseñadas para mitigar riesgos, reducir costos "
            + "y garantizar la continuidad del negocio.",
            "Nuestro compromiso es aportar valor medible a su organización y asegurar el "
            + "cumplimiento de sus objetivos estratégicos.",
        ],
        Incluye: _incluyeComun,
        NoIncluye: _noIncluyeComun,
        NotaGeneral: NotaGeneralComun,
        Seguro:
            "Eduardo Botero Soto S.A. tiene contratado un seguro que ampara sus "
            + "obligaciones derivadas del contrato de transporte conforme los parámetros del "
            + "código de comercio. El usuario se compromete a contar con una póliza de "
            + "mercancías para asegurar los riesgos y pérdidas de las mercancías objeto de "
            + "las operaciones de transporte.",
        StandBy:
            "Se consideran Stand By los tiempos adicionales a los tiempos libres, por "
            + "causas imputables al cliente en proceso de cargue, descargue, aduanales o "
            + "documentales, siempre que excedan las 12 horas hábiles. El costo estará sujeto "
            + "a las normas vigentes.",
        Vigencia: VigenciaComun,
        Facturacion: FacturacionComun);

    /// <summary>
    /// Imagen alusiva por línea (<c>IMG_LINEA</c>), por la clave normalizada de la línea (sin tildes y en minúsculas). El
    /// valor es la clave del recurso embebido (JPEG de 1200 px de ancho, ver <c>clsRecursosPdf</c>).
    /// </summary>
    public static readonly FrozenDictionary<string, string> ImagenPorLinea = new Dictionary<string, string>
    {
        ["nacional"] = "nacional",
        ["distribucion"] = "distribucion",
        ["almacenamiento"] = "almacenamiento",
        ["itr"] = "itr",
        ["importacion"] = "importacion",
        ["exportacion"] = "exportacion",
        ["internacional"] = "andino",
        ["andino"] = "andino",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Frase bajo la imagen por línea (<c>CAP_LINEA</c>).</summary>
    public static readonly FrozenDictionary<string, string> LeyendaPorLinea = new Dictionary<string, string>
    {
        ["importacion"] = "En Botero Soto importamos en el menor tiempo posible y de forma segura.",
        ["exportacion"] = "En Botero Soto exportamos de forma segura, siempre protegidos por nuestro candado satelital.",
        ["nacional"] = "Transporte nacional seguro, confiable y a tiempo en todo el país.",
        ["internacional"] = "Operación internacional andina con acompañamiento y seguridad en frontera.",
        ["andino"] = "Operación internacional andina con acompañamiento y seguridad en frontera.",
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
