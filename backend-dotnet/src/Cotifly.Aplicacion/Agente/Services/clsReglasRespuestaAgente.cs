using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Comun;
using Microsoft.Extensions.AI;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>Un fallo de validación: <see cref="Codigo"/> estable (métricas y logs) y <see cref="Detalle"/> para corregir al modelo.</summary>
public sealed record clsFalloValidacion(string Codigo, string Detalle);

/// <summary>Resultado de <see cref="clsReglasRespuestaAgente.Validar"/>: la respuesta si pasó todas las reglas, o sus fallos.</summary>
public sealed record clsValidacionRespuesta(clsRespuestaAgenteEstructurada? Respuesta, IReadOnlyList<clsFalloValidacion> Fallos)
{
    public bool EsValida => Respuesta is not null;
}

/// <summary>
/// Esquema y reglas de la respuesta final del agente (ADR-003 "Salida estructurada"). La validación es manual
/// aunque el proveedor haya recibido el JSON Schema: no todos lo cumplen (o lo aceptan) y las reglas de negocio no caben en él.
/// </summary>
/// <remarks>
/// Reglas, en orden (las de negocio solo se evalúan si la forma es correcta):
/// <list type="number">
/// <item>Forma: JSON completo (se toleran las cercas <c>```json</c>), un objeto, sin campos desconocidos ni repetidos. Si la
/// respuesta trae texto alrededor del JSON (prosa y un bloque de código, como Gemini vía OmniRoute), se toma el primer objeto
/// embebido que cumple toda la forma, solo si es el único distinto; con dos o más (un ejemplo citado, un JSON pegado por el
/// comercial) o con ninguno, el fallo es <c>json_invalido</c>.</item>
/// <item>Campos: <c>tipo</c> ∈ {conversacional, previsualizacion, confirmacion_requerida, error}; <c>texto</c> string de 1 a 4000
/// caracteres sin contar espacios al borde; <c>requiere_confirmacion</c> booleano; <c>acciones_sugeridas</c> ausente, null o una
/// lista de {confirmar_pdf, ajustar_margen}.</item>
/// <item>Coherencia: <c>requiere_confirmacion</c> es true exactamente con previsualizacion y confirmacion_requerida; el asistente
/// por folio no calcula precios, así que una previsualizacion nunca es válida; una
/// acción sugerida exige su herramienta declarada en el turno (ninguna del asistente lo es: las
/// propuestas van en <c>accion_propuesta</c>, que arma el servidor).</item>
/// <item>Montos (<see cref="Montos"/>): con moneda (<c>$</c>, <c>COP</c>, <c>USD</c>, <c>US$</c>, <c>EUR</c>, <c>€</c>, pesos,
/// dólares, euros), con sufijo de millones (<c>millones</c>, <c>mill</c>, <c>M</c>, <c>MM</c>), de miles de millones (<c>mil
/// millones</c>) o de miles (<c>mil</c>, <c>k</c>), con separadores de miles (punto, coma, apóstrofo o espacio normal, duro o fino)
/// de 1.000.000 o más, de 1.000 o más en la ventana de 40 caracteres tras "precio", "costo", "cuesta", "utilidad", "tarifa", "valor",
/// "vale", "total", "flete", "descuento", "IVA", "TRM", "tasa", "tipo de cambio", "monto", "importe", "presupuesto", "cobr…" o
/// "pag…" (todos los números de la ventana), y en letras ("tres millones", "dos mil quinientos pesos"). Los dígitos de
/// otros sistemas (<c>３</c>) se leen como ASCII y un número ilegible es un monto sin respaldo. Se descartan fechas, años sueltos tras
/// una palabra clave, números tras NIT/tel/teléfono/cel/celular/cédula/CC/folio/llamar, los seguidos de una unidad (kg, ton, km,
/// metros, días, horas, vehículos, posiciones, viajes, %) y las cifras dentro de un código (<c>3S3</c>, <c>900.047.228-1</c>), de un
/// radicado u orden o de una fecha con puntos. Con una palabra de dinero justo antes ("precio 950") cualquier cifra es monto. Todo
/// se lee sobre <see cref="Normalizar"/> (dígitos Unicode, separadores de ancho completo y números partidos por espacios o saltos de
/// línea). <b>Ninguna salida de herramienta respalda un monto</b> (no le entregan montos al modelo): un monto
/// solo pasa si su valor es EXACTAMENTE el de uno que el comercial escribió como dinero, si está en letras y el comercial escribió el
/// mismo texto, o si no lleva marca de dinero y es parte de un texto completo de las salidas que el modelo repite ("3M Colombia").
/// El estado de la conversación no respalda montos: no los tiene.</item>
/// <item>Folios: todo <c>PCN-…</c> de <c>texto</c> debe aparecer en el estado, en las salidas de las herramientas o en los mensajes
/// del comercial.</item>
/// </list>
/// </remarks>
public static partial class clsReglasRespuestaAgente
{
    public const string NombreEsquema = "respuesta_agente";
    public const string DescripcionEsquema = "Respuesta final del asistente de cotización de Botero Soto para el comercial.";

    // Códigos de fallo (estables: etiquetas de métricas y logs).
    public const string RespuestaVacia = "respuesta_vacia";
    public const string JsonInvalido = "json_invalido";
    public const string NoEsObjeto = "no_es_objeto";
    public const string CampoDesconocido = "campo_desconocido";
    public const string CampoRepetido = "campo_repetido";
    public const string CampoFaltante = "campo_faltante";
    public const string TipoIncorrecto = "tipo_incorrecto";
    public const string EnumInvalido = "enum_invalido";
    public const string TextoVacio = "texto_vacio";
    public const string TextoLargo = "texto_largo";
    public const string AccionInvalida = "accion_invalida";
    public const string AccionNoDisponible = "accion_no_disponible";
    public const string IncoherenciaConfirmacion = "incoherencia_confirmacion";
    public const string PrevisualizacionSinPrecio = "previsualizacion_sin_precio";
    public const string MontoNoRespaldado = "monto_no_respaldado";
    public const string FolioNoRespaldado = "folio_no_respaldado";

    private const string CampoTipo = "tipo";
    private const string CampoTexto = "texto";
    private const string CampoRequiere = "requiere_confirmacion";
    private const string CampoAcciones = "acciones_sugeridas";
    private static readonly string[] _campos = [CampoTipo, CampoTexto, CampoRequiere, CampoAcciones];

    /// <summary>Objetos embebidos que se prueban, como máximo, en una respuesta con texto alrededor del JSON.</summary>
    private const int MaximoCandidatos = 100;

    /// <summary>Reinicios de la búsqueda tras una llave sin cerrar (acota el costo con texto patológico).</summary>
    private const int MaximoReinicios = 20;

    /// <summary>Herramienta del plugin que respalda cada acción sugerida.</summary>
    public static readonly IReadOnlyDictionary<AccionSugeridaAgente, string> HerramientaDeAccion = new Dictionary<AccionSugeridaAgente, string>
    {
        [AccionSugeridaAgente.ConfirmarPdf] = clsNombresHerramientas.GenerarPdf,
        [AccionSugeridaAgente.AjustarMargen] = clsNombresHerramientas.RegistrarAjusteMargen,
    };

    /// <summary>JSON del esquema: snake_case, enums como texto snake_case, sin tildes escapadas.</summary>
    public static readonly JsonSerializerOptions OpcionesJson = CrearOpciones();

    private static readonly Dictionary<string, TipoRespuestaAgente> _tipos = Valores<TipoRespuestaAgente>();
    private static readonly Dictionary<string, AccionSugeridaAgente> _acciones = Valores<AccionSugeridaAgente>();
    private static readonly Lazy<JsonObject> _nodoEsquema = new(() => CrearEsquema(estricto: false));
    private static readonly Lazy<JsonObject> _nodoEsquemaEstricto = new(() => CrearEsquema(estricto: true));
    private static readonly Lazy<JsonElement> _esquema = new(() => JsonSerializer.SerializeToElement(_nodoEsquema.Value, OpcionesJson));
    private static readonly Lazy<JsonElement> _esquemaEstricto = new(() => JsonSerializer.SerializeToElement(_nodoEsquemaEstricto.Value, OpcionesJson));

    /// <summary>
    /// JSON Schema de <see cref="clsRespuestaAgenteEstructurada"/> generado con <see cref="AIJsonUtilities"/>: todos los campos
    /// requeridos (acciones admite null), sin propiedades adicionales, <c>texto</c> con <c>minLength</c> 1 y <c>maxLength</c> 4000,
    /// y <c>acciones_sugeridas</c> con <c>maxItems</c> 2 y <c>uniqueItems</c>.
    /// </summary>
    public static JsonElement Esquema => _esquema.Value;

    /// <summary>
    /// Variante para el modo estricto (<c>IA:JsonSchemaEstricto</c>): sin <c>minLength</c>, <c>maxLength</c> ni <c>uniqueItems</c>,
    /// que el modo estricto de OpenAI no admite (la validación manual los sigue exigiendo).
    /// </summary>
    public static JsonElement EsquemaEstricto => _esquemaEstricto.Value;

    /// <summary>El esquema como texto, sin tildes escapadas (va en la instrucción del modo sin JSON Schema).</summary>
    public static string EsquemaTexto => _nodoEsquema.Value.ToJsonString(OpcionesJson);

    /// <summary>Formato de la llamada final: <c>ChatResponseFormat.ForJsonSchema</c> con <see cref="Esquema"/>.</summary>
    public static ChatResponseFormat Formato => ChatResponseFormat.ForJsonSchema(Esquema, NombreEsquema, DescripcionEsquema);

    /// <summary>Formato del modo estricto (<see cref="EsquemaEstricto"/>).</summary>
    public static ChatResponseFormat FormatoEstricto => ChatResponseFormat.ForJsonSchema(EsquemaEstricto, NombreEsquema, DescripcionEsquema);

    /// <summary>Valores de <c>tipo</c> tal como van en el JSON.</summary>
    public static IReadOnlyCollection<string> Tipos => _tipos.Keys;

    /// <summary>Valores de <c>acciones_sugeridas</c> tal como van en el JSON.</summary>
    public static IReadOnlyCollection<string> Acciones => _acciones.Keys;

    /// <summary>Código JSON de un valor de enum (<c>ConfirmacionRequerida</c> → <c>confirmacion_requerida</c>).</summary>
    public static string Codigo<TEnum>(TEnum valor) where TEnum : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(valor.ToString());

    /// <summary>Valida la respuesta cruda del modelo contra el esquema y las reglas del turno. Nunca lanza.</summary>
    public static clsValidacionRespuesta Validar(string? crudo, clsContextoValidacionRespuesta contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        var fallos = new List<clsFalloValidacion>();
        var respuesta = LeerForma(crudo, fallos);
        if (respuesta is null || fallos.Count > 0)
        {
            return new clsValidacionRespuesta(null, fallos);
        }

        ValidarCoherencia(respuesta, contexto, fallos);
        ValidarRespaldo(respuesta.Texto, contexto, fallos);
        return fallos.Count == 0 ? new clsValidacionRespuesta(respuesta, []) : new clsValidacionRespuesta(null, fallos);
    }

    private static clsRespuestaAgenteEstructurada? LeerForma(string? crudo, List<clsFalloValidacion> fallos)
    {
        if (string.IsNullOrWhiteSpace(crudo))
        {
            fallos.Add(new(RespuestaVacia, "La respuesta llegó vacía: se esperaba un objeto JSON."));
            return null;
        }

        var directos = new List<clsFalloValidacion>();
        var directa = LeerJson(SinCercas(crudo), directos);
        if (directa is not null || directos.TrueForAll(f => f.Codigo != JsonInvalido))
        {
            fallos.AddRange(directos);
            return directa;
        }

        // Texto alrededor del JSON: Gemini vía OmniRoute ignora response_format y responde prosa con un bloque ```json. Se acepta
        // el objeto embebido que cumple toda la forma solo si es el ÚNICO distinto: con dos (un ejemplo citado, un JSON que pegó el
        // comercial) no se adivina cuál es la respuesta y el fallo es json_invalido, que fuerza el reintento de corrección.
        clsRespuestaAgenteEstructurada? unica = null;
        string? claveUnica = null;
        List<clsFalloValidacion>? delPrimerObjeto = null;
        foreach (var candidato in ObjetosEmbebidos(crudo))
        {
            var fallosCandidato = new List<clsFalloValidacion>();
            if (LeerJson(candidato, fallosCandidato) is { } embebida && fallosCandidato.Count == 0)
            {
                var clave = Clave(embebida);
                if (claveUnica is null)
                {
                    (unica, claveUnica) = (embebida, clave);
                }
                else if (!string.Equals(claveUnica, clave, StringComparison.Ordinal))
                {
                    fallos.Add(new(JsonInvalido, "La respuesta trae más de un objeto JSON con la forma de la respuesta final: responde con uno solo, sin ejemplos ni texto citado."));
                    return null;
                }

                continue;
            }

            // El primer objeto JSON embebido que no cumple la forma aporta sus fallos a la corrección (además de json_invalido).
            if (delPrimerObjeto is null && fallosCandidato.TrueForAll(f => f.Codigo != JsonInvalido))
            {
                delPrimerObjeto = fallosCandidato;
            }
        }

        if (unica is not null)
        {
            return unica;
        }

        fallos.AddRange(directos);
        fallos.AddRange(delPrimerObjeto ?? []);
        return null;
    }

    /// <summary>Identidad de una respuesta (un mismo objeto puede aparecer como bloque de código y como objeto balanceado).</summary>
    private static string Clave(clsRespuestaAgenteEstructurada respuesta) =>
        string.Join('\u0001', respuesta.Tipo.ToString(), respuesta.Texto, respuesta.RequiereConfirmacion.ToString(),
            string.Join(',', respuesta.AccionesSugeridas ?? []));

    /// <summary>
    /// Candidatos a objeto JSON dentro de un texto, en orden de aparición: el contenido de cada bloque de código y cada objeto
    /// <c>{...}</c> de primer nivel balanceado (respetando cadenas y escapes; un objeto dentro de otro, o dentro de una cadena, no es
    /// candidato aparte). Una pasada lineal por el texto; una llave sin cerrar reinicia la búsqueda justo después de ella como máximo
    /// <see cref="MaximoReinicios"/> veces y solo si queda una <c>}</c> más adelante, así miles de llaves sin cerrar no cuestan O(n²).
    /// </summary>
    private static List<string> ObjetosEmbebidos(string texto)
    {
        var candidatos = new List<(int Posicion, string Texto)>();
        try
        {
            foreach (Match bloque in ExpresionBloqueCodigo().Matches(texto))
            {
                candidatos.Add((bloque.Groups["json"].Index, bloque.Groups["json"].Value));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Texto patológico: se sigue solo con los objetos balanceados.
        }

        var ultimoCierre = texto.LastIndexOf('}');
        var desde = 0;
        for (var reinicios = 0; reinicios <= MaximoReinicios; reinicios++)
        {
            if (ObjetosDePrimerNivel(texto, desde, candidatos) is not { } abierta || abierta >= ultimoCierre)
            {
                break;
            }

            desde = abierta + 1;
        }

        return candidatos.OrderBy(c => c.Posicion).Take(MaximoCandidatos).Select(c => c.Texto).ToList();
    }

    /// <summary>
    /// Agrega los objetos de primer nivel desde <paramref name="desde"/>. Devuelve la posición de la llave de primer nivel que quedó
    /// sin cerrar al final del texto, o null si todas cerraron.
    /// </summary>
    private static int? ObjetosDePrimerNivel(string texto, int desde, List<(int Posicion, string Texto)> candidatos)
    {
        var profundidad = 0;
        var enCadena = false;
        var inicio = -1;
        for (var i = desde; i < texto.Length; i++)
        {
            var c = texto[i];
            if (profundidad == 0)
            {
                if (c == '{')
                {
                    (profundidad, inicio, enCadena) = (1, i, false);
                }

                continue;
            }

            if (enCadena)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    enCadena = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    enCadena = true;
                    break;
                case '{':
                    profundidad++;
                    break;
                case '}' when --profundidad == 0:
                    candidatos.Add((inicio, texto[inicio..(i + 1)]));
                    break;
            }
        }

        return profundidad > 0 ? inicio : null;
    }

    private static clsRespuestaAgenteEstructurada? LeerJson(string json, List<clsFalloValidacion> fallos)
    {
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            fallos.Add(new(JsonInvalido, "La respuesta no es un JSON completo y válido (quedó incompleta o trae texto fuera del objeto)."));
            return null;
        }

        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                fallos.Add(new(NoEsObjeto, "La respuesta debe ser un objeto JSON con tipo, texto, requiere_confirmacion y acciones_sugeridas."));
                return null;
            }

            var vistos = new HashSet<string>(StringComparer.Ordinal);
            foreach (var propiedad in raiz.EnumerateObject())
            {
                if (!_campos.Contains(propiedad.Name, StringComparer.Ordinal))
                {
                    var nombre = propiedad.Name.Length > 40 ? propiedad.Name[..40] + "…" : propiedad.Name;
                    fallos.Add(new(CampoDesconocido, $"El campo '{nombre}' no existe en el esquema; usa solo tipo, texto, requiere_confirmacion y acciones_sugeridas."));
                }
                else if (!vistos.Add(propiedad.Name))
                {
                    fallos.Add(new(CampoRepetido, $"El campo '{propiedad.Name}' aparece más de una vez."));
                }
            }

            var tipo = LeerTipo(raiz, fallos);
            var texto = LeerTexto(raiz, fallos);
            var requiere = LeerRequiere(raiz, fallos);
            var acciones = LeerAcciones(raiz, fallos);
            if (fallos.Count > 0 || tipo is null || texto is null || requiere is null)
            {
                return null;
            }

            return new clsRespuestaAgenteEstructurada
            {
                Tipo = tipo.Value,
                Texto = texto,
                RequiereConfirmacion = requiere.Value,
                AccionesSugeridas = acciones is { Count: > 0 } ? acciones : null,
            };
        }
    }

    private static TipoRespuestaAgente? LeerTipo(JsonElement raiz, List<clsFalloValidacion> fallos)
    {
        if (!raiz.TryGetProperty(CampoTipo, out var valor) || valor.ValueKind == JsonValueKind.Null)
        {
            fallos.Add(new(CampoFaltante, "Falta el campo obligatorio 'tipo'."));
            return null;
        }

        if (valor.ValueKind != JsonValueKind.String)
        {
            fallos.Add(new(TipoIncorrecto, "El campo 'tipo' debe ser un texto."));
            return null;
        }

        if (!_tipos.TryGetValue(valor.GetString()!, out var tipo))
        {
            fallos.Add(new(EnumInvalido, $"El tipo '{Recortar(valor.GetString()!)}' no es válido; usa uno de: {string.Join(", ", _tipos.Keys)}."));
            return null;
        }

        return tipo;
    }

    private static string? LeerTexto(JsonElement raiz, List<clsFalloValidacion> fallos)
    {
        if (!raiz.TryGetProperty(CampoTexto, out var valor) || valor.ValueKind == JsonValueKind.Null)
        {
            fallos.Add(new(CampoFaltante, "Falta el campo obligatorio 'texto'."));
            return null;
        }

        if (valor.ValueKind != JsonValueKind.String)
        {
            fallos.Add(new(TipoIncorrecto, "El campo 'texto' debe ser un texto."));
            return null;
        }

        var texto = valor.GetString()!.Trim();
        if (texto.Length == 0)
        {
            fallos.Add(new(TextoVacio, "El campo 'texto' está vacío: escribe la respuesta para el comercial."));
            return null;
        }

        if (texto.Length > clsRespuestaAgenteEstructurada.LargoMaximoTexto)
        {
            fallos.Add(new(TextoLargo, $"El campo 'texto' tiene {texto.Length} caracteres; el máximo es {clsRespuestaAgenteEstructurada.LargoMaximoTexto}."));
            return null;
        }

        return texto;
    }

    private static bool? LeerRequiere(JsonElement raiz, List<clsFalloValidacion> fallos)
    {
        if (!raiz.TryGetProperty(CampoRequiere, out var valor) || valor.ValueKind == JsonValueKind.Null)
        {
            fallos.Add(new(CampoFaltante, "Falta el campo obligatorio 'requiere_confirmacion'."));
            return null;
        }

        if (valor.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            fallos.Add(new(TipoIncorrecto, "El campo 'requiere_confirmacion' debe ser booleano (true o false, sin comillas)."));
            return null;
        }

        return valor.GetBoolean();
    }

    private static List<AccionSugeridaAgente>? LeerAcciones(JsonElement raiz, List<clsFalloValidacion> fallos)
    {
        if (!raiz.TryGetProperty(CampoAcciones, out var valor) || valor.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (valor.ValueKind != JsonValueKind.Array)
        {
            fallos.Add(new(TipoIncorrecto, "El campo 'acciones_sugeridas' debe ser una lista o null."));
            return null;
        }

        var acciones = new List<AccionSugeridaAgente>();
        foreach (var elemento in valor.EnumerateArray())
        {
            if (elemento.ValueKind != JsonValueKind.String || !_acciones.TryGetValue(elemento.GetString()!, out var accion))
            {
                var nombre = elemento.ValueKind == JsonValueKind.String ? Recortar(elemento.GetString()!) : elemento.ValueKind.ToString();
                fallos.Add(new(AccionInvalida, $"La acción '{nombre}' no existe; las únicas acciones son: {string.Join(", ", _acciones.Keys)}."));
                continue;
            }

            if (!acciones.Contains(accion))
            {
                acciones.Add(accion);
            }
        }

        return acciones;
    }

    private static void ValidarCoherencia(clsRespuestaAgenteEstructurada respuesta, clsContextoValidacionRespuesta contexto, List<clsFalloValidacion> fallos)
    {
        var pideConfirmacion = respuesta.Tipo is TipoRespuestaAgente.Previsualizacion or TipoRespuestaAgente.ConfirmacionRequerida;
        if (respuesta.RequiereConfirmacion != pideConfirmacion)
        {
            fallos.Add(new(IncoherenciaConfirmacion,
                "requiere_confirmacion debe ser true solo con tipo previsualizacion o confirmacion_requerida, y false con los demás tipos."));
        }

        if (respuesta.Tipo == TipoRespuestaAgente.Previsualizacion)
        {
            fallos.Add(new(PrevisualizacionSinPrecio,
                "El asistente no calcula precios ni previsualiza cotizaciones: no uses tipo previsualizacion; responde con tipo conversacional."));
        }

        foreach (var accion in respuesta.AccionesSugeridas ?? [])
        {
            if (!contexto.HerramientasDeclaradas.Contains(HerramientaDeAccion[accion]))
            {
                fallos.Add(new(AccionNoDisponible,
                    $"La acción '{Codigo(accion)}' no está disponible en esta conversación; usa acciones_sugeridas null (las propuestas las arma el servidor)."));
            }
        }
    }

    private static void ValidarRespaldo(string texto, clsContextoValidacionRespuesta contexto, List<clsFalloValidacion> fallos)
    {
        var respaldos = Respaldos(contexto);

        foreach (Match folio in ExpresionFolio().Matches(texto))
        {
            if (!respaldos.Cadenas.Any(c => c.Contains(folio.Value, StringComparison.OrdinalIgnoreCase)))
            {
                fallos.Add(new(FolioNoRespaldado,
                    $"El folio '{folio.Value}' no aparece en las salidas de las herramientas ni en el estado; el folio lo genera el servidor, no lo inventes."));
            }
        }

        var normalizado = Normalizar(texto);
        foreach (var monto in Montos(texto))
        {
            if (!Respaldado(monto, respaldos, normalizado))
            {
                fallos.Add(new(MontoNoRespaldado,
                    $"El monto '{monto.Fragmento}' no lo escribió el comercial como dinero; tú no conoces montos: remite al resumen del sistema y no escribas valores en dinero."));
            }
        }
    }

    /// <summary>
    /// Un monto de dinero encontrado en un texto: el fragmento (del texto normalizado), sus valores posibles según los separadores, su
    /// posición en el texto normalizado y si lleva marca de dinero (moneda o palabra de dinero). Un monto en letras ("tres millones") o
    /// un número que no se puede leer no tiene candidatos.
    /// </summary>
    public sealed record clsMonto(string Fragmento, IReadOnlyList<decimal> Candidatos)
    {
        /// <summary>Posición del fragmento en <see cref="Normalizar"/> del texto.</summary>
        public int Inicio { get; init; }

        /// <summary>¿Lleva moneda o palabra de dinero? Entonces solo lo respalda un monto que el comercial escribió como dinero.</summary>
        public bool ConMarca { get; init; }
    }

    /// <summary>
    /// Texto normalizado para leer cifras: dígitos Unicode a ASCII, separadores de ancho completo y árabes a punto o
    /// coma, y sin los espacios ni saltos de línea que parten un número (<c>$ 1 . 400 . 000</c>, <c>3⏎400⏎000</c>,
    /// <c>3 400 000</c>). Los folios <c>PCN-…</c> se cubren con espacios del mismo largo (sus dígitos no son montos).
    /// </summary>
    public static string Normalizar(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return string.Empty;
        }

        var caracteres = new System.Text.StringBuilder(texto.Length);
        foreach (var caracter in texto)
        {
            caracteres.Append(caracter switch
            {
                _ when char.IsDigit(caracter) && caracter is < '0' or > '9' => (char)('0' + (int)char.GetNumericValue(caracter)),
                '，' or '٫' or '،' => ',',
                '．' or '٬' => '.',
                _ => caracter,
            });
        }

        var unido = ExpresionEspacioDeMiles().Replace(ExpresionEspacioJuntoASeparador().Replace(caracteres.ToString(), string.Empty), ".");
        return ExpresionFolio().Replace(unido, m => new string(' ', m.Length));
    }

    /// <summary>
    /// Montos de dinero de un texto (reglas en el resumen de la clase), leídos sobre <see cref="Normalizar"/>. Se usa para el texto del
    /// modelo, para los mensajes del comercial (solo lo que él escribió como dinero respalda) y en los textos que redacta la IA para el
    /// PDF (<c>clsReglasTextosCotizacion</c>).
    /// </summary>
    public static IReadOnlyList<clsMonto> Montos(string? texto)
    {
        var montos = new List<clsMonto>();
        if (string.IsNullOrWhiteSpace(texto))
        {
            return montos;
        }

        var limpio = Normalizar(texto);
        foreach (Match coincidencia in ExpresionNumero().Matches(limpio))
        {
            if (Monto(limpio, coincidencia) is { } monto)
            {
                montos.Add(monto);
            }
        }

        // Montos en letras. Con unidad ("tres millones", "dos mil quinientos pesos") siempre; sin unidad
        // ("doscientos mil") solo si llevan "mil" y van tras una palabra de dinero ("el flete queda en doscientos mil").
        foreach (Match enLetras in ExpresionMontoEnLetras().Matches(limpio))
        {
            var conUnidad = enLetras.Groups["u"].Success;
            if (conUnidad || (ExpresionMilEnLetras().IsMatch(enLetras.Value) && ExpresionPalabraDinero().IsMatch(Ventana(limpio, enLetras.Index))))
            {
                montos.Add(new clsMonto(enLetras.Value.Trim(), []) { Inicio = enLetras.Index, ConMarca = true });
            }
        }

        return montos;
    }

    /// <summary>El monto de una coincidencia de <see cref="ExpresionNumero"/>, o null si el número no es dinero.</summary>
    private static clsMonto? Monto(string limpio, Match coincidencia)
    {
        var numero = coincidencia.Groups["n"].Value;
        var inicio = coincidencia.Index;
        var finNumero = inicio + numero.Length;
        var fin = inicio + coincidencia.Length;
        var previo = inicio > 0 ? limpio[inicio - 1] : ' ';
        var anterior = inicio > 1 ? limpio[inicio - 2] : ' ';
        var siguiente = finNumero < limpio.Length ? limpio[finNumero] : ' ';

        // Fechas (25/09/2026, 08.10.2026) y sufijos de códigos (900.047.228-1, ABC-12). Un signo menos suelto ("de -150.000") sí se revisa.
        if (previo == '/' || siguiente == '/' || (previo == '-' && char.IsLetterOrDigit(anterior)) || ExpresionFechaConPuntos().IsMatch(numero))
        {
            return null;
        }

        var antes = Ventana(limpio, inicio);
        var despues = limpio[fin..Math.Min(limpio.Length, fin + 20)];
        if (ExpresionContextoNoMonetario().IsMatch(antes) || ExpresionUnidad().IsMatch(despues))
        {
            return null;
        }

        var monedaAntes = ExpresionMonedaAntes().Match(antes);
        var conMoneda = monedaAntes.Success || ExpresionMonedaDespues().IsMatch(despues);
        var multiplicador = Multiplicador(coincidencia);
        if (!conMoneda && (char.IsLetter(previo) || (multiplicador == 1m && char.IsLetter(siguiente))))
        {
            return null; // parte de un código (v2, A1, 3S3, 2S2...)
        }

        var candidatos = clsNumerosDelMensaje.CandidatosDe(numero).Select(c => c * multiplicador).Distinct().ToList();

        // Sin separadores y con 9 cifras o más, sin moneda: un NIT o un teléfono, no un precio.
        var identificador = numero.All(char.IsAsciiDigit) && numero.Length >= 9;
        var palabraJusto = ExpresionPalabraDineroJusto().IsMatch(antes);
        var palabraVentana = !identificador && !EsAnio(numero) && (candidatos.Count == 0 || candidatos.Any(c => c >= 1000m))
            && ExpresionPalabraDinero().IsMatch(antes);
        var conMarca = conMoneda || (!identificador && !EsAnio(numero) && palabraJusto) || palabraVentana;
        var esMonto = conMarca || multiplicador > 1m || (!identificador && ExpresionMillonesAgrupados().IsMatch(numero));
        if (!esMonto)
        {
            return null;
        }

        // Un número que no se puede leer queda sin candidatos: nunca tiene respaldo numérico.
        var desde = monedaAntes.Success ? inicio - monedaAntes.Length : inicio;
        return new clsMonto(limpio[desde..fin].Trim(), candidatos) { Inicio = desde, ConMarca = conMarca };
    }

    /// <summary>Millones (<c>millones</c>, <c>M</c>, <c>MM</c>), miles de millones (<c>mil millones</c>) o miles (<c>mil</c>, <c>k</c>).</summary>
    private static decimal Multiplicador(Match coincidencia) =>
        coincidencia.Groups["mm"].Success ? 1_000_000_000m
        : coincidencia.Groups["m"].Success ? 1_000_000m
        : coincidencia.Groups["k"].Success ? 1_000m
        : 1m;

    /// <summary>Los 40 caracteres antes del número, sin pasar del inicio de la frase (<c>. </c>, <c>;</c>, <c>?</c>, <c>!</c>, salto).</summary>
    private static string Ventana(string texto, int posicion)
    {
        var desde = Math.Max(0, posicion - 40);
        var ventana = texto[desde..posicion];
        var cortes = ExpresionFinFrase().Matches(ventana);
        return cortes.Count == 0 ? ventana : ventana[(cortes[^1].Index + cortes[^1].Length)..];
    }

    /// <summary>
    /// ¿Algo respalda el monto? Las herramientas del asistente nunca le entregan montos al modelo, así que ningún
    /// campo de sus salidas respalda dinero (ni <c>"version": 1</c> ni <c>"cantidad": 2</c> dejan pasar "$1" o "USD 2"):
    /// <list type="bullet">
    /// <item>Un número respalda solo si uno de sus valores posibles es <b>exactamente</b> (sin redondear) el de un monto que el
    /// comercial escribió como dinero (moneda, sufijo, palabra de dinero o millones agrupados), normalizado igual.</item>
    /// <item>Un monto en letras, si el comercial escribió ese mismo texto.</item>
    /// <item>Un número SIN marca de dinero ("3M") pasa si forma parte de un texto completo de las salidas que el modelo repite tal
    /// cual ("3M Colombia"). Con marca ("el precio es 3M") nunca.</item>
    /// </list>
    /// </summary>
    private static bool Respaldado(clsMonto monto, clsRespaldos respaldos, string normalizado)
    {
        if (monto.Candidatos.Any(c => respaldos.MontosDelComercial.Any(n => n == c)))
        {
            return true;
        }

        if (monto.Candidatos.Count == 0 && !monto.Fragmento.Any(char.IsDigit))
        {
            return EstaLiteral(monto.Fragmento, respaldos.MensajesDelComercial);
        }

        return !monto.ConMarca && DentroDeUnTextoDeHerramienta(monto, respaldos.TextosHerramientas, normalizado);
    }

    /// <summary>¿El fragmento aparece como palabra completa (sin distinguir mayúsculas) en alguno de los textos?</summary>
    private static bool EstaLiteral(string fragmento, IReadOnlyList<string> textos)
    {
        var patron = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(fragmento)}(?![\p{{L}}\p{{N}}])";
        return textos.Any(t => Regex.IsMatch(t, patron, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));
    }

    /// <summary>¿El monto está dentro de una aparición, en el texto del modelo, de un texto completo de las salidas que lo contiene?</summary>
    private static bool DentroDeUnTextoDeHerramienta(clsMonto monto, IReadOnlyList<string> textosHerramientas, string normalizado)
    {
        var finMonto = monto.Inicio + monto.Fragmento.Length;
        foreach (var texto in textosHerramientas)
        {
            if (texto.Length <= monto.Fragmento.Length || texto.Length > 200 || !EstaLiteral(monto.Fragmento, [texto]))
            {
                continue;
            }

            for (var desde = normalizado.IndexOf(texto, StringComparison.OrdinalIgnoreCase); desde >= 0;
                 desde = normalizado.IndexOf(texto, desde + 1, StringComparison.OrdinalIgnoreCase))
            {
                if (desde <= monto.Inicio && finMonto <= desde + texto.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Lo que respalda un monto o un folio en la respuesta final.</summary>
    /// <param name="MontosDelComercial">Valores de los montos que el comercial escribió como dinero (sin redondear).</param>
    /// <param name="Cadenas">Textos que respaldan un folio: el folio del estado, las salidas de las herramientas y los mensajes del comercial.</param>
    /// <param name="TextosHerramientas">Textos de las salidas de las herramientas, normalizados (nunca traen montos).</param>
    /// <param name="MensajesDelComercial">Mensajes del comercial normalizados (respaldan un monto en letras que él escribió).</param>
    private sealed record clsRespaldos(List<decimal> MontosDelComercial, List<string> Cadenas, List<string> TextosHerramientas, List<string> MensajesDelComercial);

    /// <summary>Respaldos del turno: montos que el comercial escribió como dinero, textos de las salidas y el folio del estado.</summary>
    private static clsRespaldos Respaldos(clsContextoValidacionRespuesta contexto)
    {
        var montos = new List<decimal>();
        var cadenas = new List<string>();
        var textosHerramientas = new List<string>();
        var estado = contexto.Estado;
        if (!string.IsNullOrWhiteSpace(estado.FolioEnProgreso))
        {
            cadenas.Add(estado.FolioEnProgreso);
        }

        foreach (var salida in contexto.Salidas)
        {
            var modelo = salida.ParaModelo();
            Recolectar(JsonSerializer.SerializeToElement(modelo, modelo.GetType(), clsJsonAplicacion.SnakeCase), textosHerramientas);
        }

        cadenas.AddRange(textosHerramientas);
        var mensajes = new List<string>();
        foreach (var mensaje in contexto.MensajesDelComercial)
        {
            foreach (var monto in Montos(mensaje))
            {
                montos.AddRange(monto.Candidatos);
            }

            cadenas.Add(mensaje);
            mensajes.Add(Normalizar(mensaje));
        }

        return new clsRespaldos(montos, cadenas, textosHerramientas.Select(Normalizar).ToList(), mensajes);
    }

    private static void Recolectar(JsonElement elemento, List<string> cadenas)
    {
        switch (elemento.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var propiedad in elemento.EnumerateObject())
                {
                    Recolectar(propiedad.Value, cadenas);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in elemento.EnumerateArray())
                {
                    Recolectar(item, cadenas);
                }

                break;
            case JsonValueKind.String:
                cadenas.Add(elemento.GetString()!);
                break;
        }
    }

    private static bool EsAnio(string numero) =>
        numero.Length == 4 && int.TryParse(numero, NumberStyles.None, CultureInfo.InvariantCulture, out var anio) && anio is >= 2000 and <= 2100;

    private static string SinCercas(string crudo)
    {
        try
        {
            var coincidencia = ExpresionCercas().Match(crudo);
            return coincidencia.Success ? coincidencia.Groups["json"].Value : crudo.Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return crudo.Trim();
        }
    }

    private static string Recortar(string valor) => valor.Length > 40 ? valor[..40] + "…" : valor;

    private static Dictionary<string, TEnum> Valores<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().ToDictionary(v => Codigo(v), v => v, StringComparer.Ordinal);

    private static JsonSerializerOptions CrearOpciones()
    {
        var opciones = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        opciones.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        opciones.MakeReadOnly();
        return opciones;
    }

    private static JsonObject CrearEsquema(bool estricto)
    {
        var generado = AIJsonUtilities.CreateJsonSchema(
            typeof(clsRespuestaAgenteEstructurada),
            DescripcionEsquema,
            serializerOptions: OpcionesJson,
            inferenceOptions: new AIJsonSchemaCreateOptions
            {
                TransformOptions = new AIJsonSchemaTransformOptions { DisallowAdditionalProperties = true, RequireAllProperties = true },
            });
        var esquema = JsonNode.Parse(generado.GetRawText())!.AsObject();
        var propiedades = esquema["properties"]!.AsObject();
        var acciones = propiedades[CampoAcciones]!.AsObject();
        acciones["maxItems"] = clsRespuestaAgenteEstructurada.MaximoAcciones;
        if (!estricto)
        {
            var texto = propiedades[CampoTexto]!.AsObject();
            texto["minLength"] = 1;
            texto["maxLength"] = clsRespuestaAgenteEstructurada.LargoMaximoTexto;
            acciones["uniqueItems"] = true;
        }

        return esquema;
    }

    [GeneratedRegex(@"(?<![0-9A-Za-z-])PCN-[0-9A-Za-z]+(?:-[0-9A-Za-z]+)*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionFolio();

    // Un número (con separadores de miles o decimales; miles también con espacio normal, duro o fino: "3 109 756") y,
    // opcional, el sufijo: "mil millones" (mm); millones/millón/mill sin distinguir mayúsculas o M/MM solo en mayúscula (m; una "m"
    // suelta puede ser metros); o miles (k): "mil" sin distinguir mayúsculas, k o K.
    [GeneratedRegex(@"(?<!\d)(?<n>\d{1,3}(?:[    ]\d{3}(?!\d))+(?:[.,]\d{1,2}(?!\d))?|\d(?:[\d.,'’]*\d)?)(?:\s?(?:(?<mm>(?i:mil\s+millones))|(?<m>(?i:millones|millón|millon|mill)|MM|M)|(?<k>(?i:mil)|k|K))(?![A-Za-zÀ-ÿ]))?", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionNumero();

    // Montos en letras: números escritos con palabras seguidos de millón/millones o de una moneda ("tres millones",
    // "dos mil quinientos pesos", "medio millón de dólares"). "mil" sin moneda no basta: "mil gracias" no es dinero. Tras una cifra
    // ("3 mil millones") no se cuenta: ese monto ya lo lee ExpresionNumero con su sufijo.
    [GeneratedRegex(@"(?<!\p{L})(?<!\p{N}\s*)(?:un|una|uno|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez|once|doce|trece|catorce|quince|dieci\p{L}+|veinte|veinti\p{L}+|treinta|cuarenta|cincuenta|sesenta|setenta|ochenta|noventa|cien|ciento|doscient[oa]s|trescient[oa]s|cuatrocient[oa]s|quinient[oa]s|seiscient[oa]s|setecient[oa]s|ochocient[oa]s|novecient[oa]s|mil|medio)(?:\s+(?:y\s+)?(?:un|una|uno|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez|once|doce|trece|catorce|quince|dieci\p{L}+|veinte|veinti\p{L}+|treinta|cuarenta|cincuenta|sesenta|setenta|ochenta|noventa|cien|ciento|doscient[oa]s|trescient[oa]s|cuatrocient[oa]s|quinient[oa]s|seiscient[oa]s|setecient[oa]s|ochocient[oa]s|novecient[oa]s|mil|medio))*(?:\s+(?:de\s+)?(?<u>mill[oó]n(?:es)?|pesos|d[oó]lares|euros|USD|COP))?(?!\p{L})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionMontoEnLetras();

    [GeneratedRegex(@"(?:\$|€|(?<![A-Za-z])(?:COP|USD|US\$|EUR))\s?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionMonedaAntes();

    [GeneratedRegex(@"\A\s?(?:de\s)?(?:COP|USD|EUR|€|pesos|d[oó]lares|euros)(?![A-Za-z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionMonedaDespues();

    [GeneratedRegex(@"\A\d{1,3}(?:[.,'’    ]\d{3}){2,}(?:[.,]\d{1,2})?\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionMillonesAgrupados();

    [GeneratedRegex(@"\b(?:precios?|costos?|cuest\w*|utilidad(?:es)?|tarifas?|valor(?:es)?|vale|valdr\w*|total(?:es)?|fletes?|descuentos?|iva|trm|tasa|tipo de cambio|montos?|importes?|presupuestos?|cobr\w*|pag\w*)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionPalabraDinero();

    [GeneratedRegex(@"\b(?:NIT|tel|tel[eé]fono|cel|celular|c[eé]dula|CC|folio|llam\w*|whatsapp|m[oó]vil|radicados?|orden(?:es)?(?: de compra)?|pedidos?|factura|consecutivo|gu[ií]a|n[°º]|no\.)(?![\p{L}])[^\d]{0,15}\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionContextoNoMonetario();

    // Espacios o saltos de línea que parten un número ("$ 1 . 400 . 000", "3⏎400⏎000", "1⏎400.000"). Solo antes de
    // un separador o de un grupo de tres dígitos, para no pegar cifras sueltas de una lista.
    [GeneratedRegex(@"(?<=\d)\s+(?=[.,]\s*\d{3}(?!\d))|(?<=\d\s*[.,])\s+(?=\d{3}(?!\d))", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionEspacioJuntoASeparador();

    // Un espacio (o salto) como separador de miles ("3 400 000") se vuelve punto: así cuenta como millones agrupados.
    [GeneratedRegex(@"(?<=\d)\s+(?=\d{3}(?!\d))", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionEspacioDeMiles();

    // Fecha con puntos o guiones (08.10.2026, 8-10-26): no es un monto aunque vaya tras "total".
    [GeneratedRegex(@"\A\d{1,2}[.\-]\d{1,2}[.\-](?:\d{2}|\d{4})\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionFechaConPuntos();

    // Palabra de dinero JUSTO antes del número ("precio 950", "el valor queda en 3,4"): cualquier cifra es un monto, aunque sea menor
    // que 1.000. Entre la palabra y el número solo caben conectores cortos.
    [GeneratedRegex(@"\b(?:precios?|costos?|cuest\w*|tarifas?|valor(?:es)?|vale|valdr\w*|total(?:es)?|fletes?|montos?|importes?|trm|cobr\w*|pag(?:o|os|a|an|ar|ue|ar[ií]a|ar[aá]))\b(?:\s+(?:es|de|del|en|el|la|final|total|unitario|nuevo|queda\w*|ser[ií]a|ser[aá]|sale|saldr\w*|unos?|aprox\w*|son|por))*\s*[:=]?\s*\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionPalabraDineroJusto();

    [GeneratedRegex(@"(?<!\p{L})mil(?!\p{L})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionMilEnLetras();

    [GeneratedRegex(@"\A\s?(?:%|(?:kg|kgs|kilos?|kilogramos?|ton|tons|toneladas?|km|kil[oó]metros?|metros?|m2|m3|d[ií]as?|horas?|veh[ií]culos?|unidades|posiciones|viajes?|cargues?|descargues?|servicios?)(?![A-Za-zÀ-ÿ]))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionUnidad();

    [GeneratedRegex(@"[;!?¿¡\n]|\.\s", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionFinFrase();

    [GeneratedRegex(@"\A\s*```(?:json)?\s*(?<json>.*?)\s*```\s*\z", RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionCercas();

    [GeneratedRegex(@"```(?:json)?\s*(?<json>.*?)\s*```", RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionBloqueCodigo();
}
