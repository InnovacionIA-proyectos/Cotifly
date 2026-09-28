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
/// Esquema y reglas de la respuesta final del agente (HU-003.10, #16, ADR-003 "Salida estructurada"). La validación es manual
/// aunque el proveedor haya recibido el JSON Schema: no todos lo cumplen (o lo aceptan) y las reglas de negocio no caben en él.
/// </summary>
/// <remarks>
/// Reglas, en orden (las de negocio solo se evalúan si la forma es correcta):
/// <list type="number">
/// <item>Forma: JSON completo (se toleran las cercas <c>```json</c>), un objeto, sin campos desconocidos ni repetidos.</item>
/// <item>Campos: <c>tipo</c> ∈ {conversacional, previsualizacion, confirmacion_requerida, error}; <c>texto</c> string de 1 a 4000
/// caracteres sin contar espacios al borde; <c>requiere_confirmacion</c> booleano; <c>acciones_sugeridas</c> ausente, null o una
/// lista de {confirmar_pdf, ajustar_margen}.</item>
/// <item>Coherencia: <c>requiere_confirmacion</c> es true exactamente con previsualizacion y confirmacion_requerida; una
/// previsualizacion exige un precio calculado por las herramientas (estado); las acciones exigen una previsualización pendiente y
/// su herramienta declarada en el turno.</item>
/// <item>Montos (<see cref="Montos"/>): con moneda (<c>$</c>, <c>COP</c>, <c>USD</c>, <c>US$</c>, pesos, dólares), con sufijo de
/// millones (<c>millones</c>, <c>mill</c>, <c>M</c>, <c>MM</c>), con separadores de miles de 1.000.000 o más, o de 1.000 o más en
/// la ventana de 40 caracteres tras "precio", "costo", "utilidad", "tarifa", "valor", "total", "flete", "descuento", "IVA",
/// "cobr…" o "pag…" (todos los números de la ventana). Se descartan fechas, años sueltos tras una palabra clave, números tras
/// NIT/tel/teléfono/cel/celular/cédula/CC/folio/llamar y los seguidos de kg/ton/toneladas/km/%. Cada monto debe coincidir,
/// redondeado a pesos, con un número de las salidas de las herramientas del turno (o una cadena de solo dígitos), con el costo,
/// el precio, la utilidad o el NIT del estado, o con un monto que el comercial escribió como dinero.</item>
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

        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(SinCercas(crudo), new JsonDocumentOptions { MaxDepth = 16 });
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

        if (respuesta.Tipo == TipoRespuestaAgente.Previsualizacion && contexto.Estado.PrecioFinal is null)
        {
            fallos.Add(new(PrevisualizacionSinPrecio,
                "No hay un precio calculado por calcular_precio: no uses tipo previsualizacion; pide los datos que faltan (tipo conversacional)."));
        }

        foreach (var accion in respuesta.AccionesSugeridas ?? [])
        {
            if (!contexto.Estado.ConfirmacionPendiente || !respuesta.RequiereConfirmacion)
            {
                fallos.Add(new(AccionNoDisponible,
                    $"La acción '{Codigo(accion)}' solo se sugiere con una previsualización pendiente de confirmar; usa acciones_sugeridas null."));
            }
            else if (!contexto.HerramientasDeclaradas.Contains(HerramientaDeAccion[accion]))
            {
                fallos.Add(new(AccionNoDisponible, $"La acción '{Codigo(accion)}' no está disponible en esta conversación."));
            }
        }
    }

    private static void ValidarRespaldo(string texto, clsContextoValidacionRespuesta contexto, List<clsFalloValidacion> fallos)
    {
        var (numeros, cadenas) = Respaldos(contexto);

        foreach (Match folio in ExpresionFolio().Matches(texto))
        {
            if (!cadenas.Any(c => c.Contains(folio.Value, StringComparison.OrdinalIgnoreCase)))
            {
                fallos.Add(new(FolioNoRespaldado,
                    $"El folio '{folio.Value}' no aparece en las salidas de las herramientas ni en el estado; el folio lo genera el servidor, no lo inventes."));
            }
        }

        foreach (var monto in Montos(texto))
        {
            if (!monto.Candidatos.Any(c => numeros.Any(n => Coinciden(c, n))))
            {
                fallos.Add(new(MontoNoRespaldado,
                    $"El monto '{monto.Fragmento}' no aparece en las salidas de las herramientas ni en el estado resuelto; usa solo los valores que calculó el servidor, tal cual."));
            }
        }
    }

    /// <summary>Un monto de dinero encontrado en un texto: el fragmento tal cual y sus valores posibles (según los separadores).</summary>
    public sealed record clsMonto(string Fragmento, IReadOnlyList<decimal> Candidatos);

    /// <summary>
    /// Montos de dinero de un texto (reglas en el resumen de la clase). Los folios <c>PCN-…</c> se quitan antes: sus dígitos no son
    /// montos. Se usa para el texto del modelo y para los mensajes del comercial (solo lo que él escribió como dinero respalda).
    /// </summary>
    public static IReadOnlyList<clsMonto> Montos(string? texto)
    {
        var montos = new List<clsMonto>();
        if (string.IsNullOrWhiteSpace(texto))
        {
            return montos;
        }

        var limpio = ExpresionFolio().Replace(texto, " ");
        foreach (Match coincidencia in ExpresionNumero().Matches(limpio))
        {
            var numero = coincidencia.Groups["n"].Value;
            var inicio = coincidencia.Index;
            var finNumero = inicio + numero.Length;
            var fin = inicio + coincidencia.Length;
            var previo = inicio > 0 ? limpio[inicio - 1] : ' ';
            var siguiente = finNumero < limpio.Length ? limpio[finNumero] : ' ';
            if (previo is '/' or '-' || siguiente is '/')
            {
                continue; // fechas (25/09/2026) y sufijos de códigos (900.047.228-1)
            }

            var antes = Ventana(limpio, inicio);
            var despues = limpio[fin..Math.Min(limpio.Length, fin + 20)];
            if (ExpresionContextoNoMonetario().IsMatch(antes) || ExpresionUnidad().IsMatch(despues))
            {
                continue;
            }

            var monedaAntes = ExpresionMonedaAntes().Match(antes);
            var conMoneda = monedaAntes.Success || ExpresionMonedaDespues().IsMatch(despues);
            if (char.IsLetter(previo) && !conMoneda)
            {
                continue; // parte de un código (v2, A1...)
            }

            var millones = coincidencia.Groups["m"].Success;
            var candidatos = clsNumerosDelMensaje.CandidatosDe(numero).Select(c => millones ? c * 1_000_000m : c).Distinct().ToList();
            if (candidatos.Count == 0)
            {
                continue;
            }

            // Sin separadores y con 9 cifras o más, sin moneda: un NIT o un teléfono, no un precio.
            var identificador = numero.All(char.IsDigit) && numero.Length >= 9;
            var esMonto = conMoneda
                || millones
                || (!identificador && ExpresionMillonesAgrupados().IsMatch(numero))
                || (!identificador && !EsAnio(numero) && candidatos.Any(c => c >= 1000m) && ExpresionPalabraDinero().IsMatch(antes));
            if (esMonto)
            {
                var desde = monedaAntes.Success ? inicio - monedaAntes.Length : inicio;
                montos.Add(new clsMonto(limpio[desde..fin].Trim(), candidatos));
            }
        }

        return montos;
    }

    /// <summary>Los 40 caracteres antes del número, sin pasar del inicio de la frase (<c>. </c>, <c>;</c>, <c>?</c>, <c>!</c>, salto).</summary>
    private static string Ventana(string texto, int posicion)
    {
        var desde = Math.Max(0, posicion - 40);
        var ventana = texto[desde..posicion];
        var cortes = ExpresionFinFrase().Matches(ventana);
        return cortes.Count == 0 ? ventana : ventana[(cortes[^1].Index + cortes[^1].Length)..];
    }

    /// <summary>
    /// Números y textos que respaldan un monto o un folio: salidas de herramientas (y sus cadenas de solo dígitos), estado (costo,
    /// precio, utilidad, NIT, folio) y los montos que el comercial escribió como dinero.
    /// </summary>
    private static (HashSet<decimal> Numeros, List<string> Cadenas) Respaldos(clsContextoValidacionRespuesta contexto)
    {
        var numeros = new HashSet<decimal>();
        var cadenas = new List<string>();
        var estado = contexto.Estado;
        // La TRM aplicada (HU-012.2) también respalda: el agente puede citarla al presentar un precio en USD.
        foreach (var valor in new[] { estado.Costo, estado.PrecioFinal, estado.PrecioFinal - estado.Costo, estado.TrmAplicada })
        {
            if (valor is { } v)
            {
                numeros.Add(v);
            }
        }

        AgregarDigitos(estado.NitCliente, numeros);
        if (!string.IsNullOrWhiteSpace(estado.FolioEnProgreso))
        {
            cadenas.Add(estado.FolioEnProgreso);
        }

        foreach (var salida in contexto.Salidas)
        {
            var modelo = salida.ParaModelo();
            Recolectar(JsonSerializer.SerializeToElement(modelo, modelo.GetType(), clsJsonAplicacion.SnakeCase), numeros, cadenas);
        }

        foreach (var mensaje in contexto.MensajesDelComercial)
        {
            foreach (var monto in Montos(mensaje))
            {
                numeros.UnionWith(monto.Candidatos);
            }

            cadenas.Add(mensaje);
        }

        return (numeros, cadenas);
    }

    private static void AgregarDigitos(string? texto, HashSet<decimal> numeros)
    {
        if (!string.IsNullOrWhiteSpace(texto) && texto.Length <= 28 && texto.All(char.IsDigit)
            && decimal.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var valor))
        {
            numeros.Add(valor);
        }
    }

    private static void Recolectar(JsonElement elemento, HashSet<decimal> numeros, List<string> cadenas)
    {
        switch (elemento.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var propiedad in elemento.EnumerateObject())
                {
                    Recolectar(propiedad.Value, numeros, cadenas);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in elemento.EnumerateArray())
                {
                    Recolectar(item, numeros, cadenas);
                }

                break;
            case JsonValueKind.Number when elemento.TryGetDecimal(out var numero):
                numeros.Add(numero);
                break;
            case JsonValueKind.String:
                var cadena = elemento.GetString()!;
                cadenas.Add(cadena);
                AgregarDigitos(cadena, numeros);
                break;
        }
    }

    /// <summary>Mismo valor redondeado a pesos (el LLM puede omitir centavos, nunca cambiar el valor).</summary>
    private static bool Coinciden(decimal escrito, decimal respaldo) =>
        Math.Round(escrito, 2, MidpointRounding.ToEven) == Math.Round(respaldo, 2, MidpointRounding.ToEven)
        || (escrito == decimal.Truncate(escrito) && escrito == Math.Round(respaldo, 0, MidpointRounding.ToEven));

    private static bool EsAnio(string numero) =>
        numero.Length == 4 && int.TryParse(numero, NumberStyles.None, CultureInfo.InvariantCulture, out var anio) && anio is >= 2000 and <= 2100;

    private static string SinCercas(string crudo)
    {
        var coincidencia = ExpresionCercas().Match(crudo);
        return coincidencia.Success ? coincidencia.Groups["json"].Value : crudo.Trim();
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

    // Un número (con separadores de miles o decimales) y, opcional, el sufijo de millones: millones/millón/mill sin distinguir
    // mayúsculas, M/MM solo en mayúscula (una "m" suelta puede ser metros).
    [GeneratedRegex(@"(?<!\d)(?<n>\d(?:[\d.,'’]*\d)?)(?:\s?(?<m>(?i:millones|millón|millon|mill)|MM|M)(?![A-Za-zÀ-ÿ]))?", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionNumero();

    [GeneratedRegex(@"(?:\$|(?<![A-Za-z])(?:COP|USD|US\$))\s?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionMonedaAntes();

    [GeneratedRegex(@"\A\s?(?:de\s)?(?:COP|USD|pesos|d[oó]lares)(?![A-Za-z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionMonedaDespues();

    [GeneratedRegex(@"\A\d{1,3}(?:[.,'’]\d{3}){2,}(?:[.,]\d{1,2})?\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionMillonesAgrupados();

    [GeneratedRegex(@"\b(?:precios?|costos?|utilidad(?:es)?|tarifas?|valor(?:es)?|total(?:es)?|fletes?|descuentos?|iva|cobr\w*|pag\w*)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionPalabraDinero();

    [GeneratedRegex(@"\b(?:NIT|tel|tel[eé]fono|cel|celular|c[eé]dula|CC|folio|llam\w*|whatsapp|m[oó]vil)\b[^\d]{0,15}\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionContextoNoMonetario();

    [GeneratedRegex(@"\A\s?(?:%|(?:kg|kgs|kilos?|ton|tons|toneladas?|km|m2|m3|d[ií]as?|horas?|veh[ií]culos?|unidades)(?![A-Za-zÀ-ÿ]))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionUnidad();

    [GeneratedRegex(@"[;!?¿¡\n]|\.\s", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionFinFrase();

    [GeneratedRegex(@"\A\s*```(?:json)?\s*(?<json>.*?)\s*```\s*\z", RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionCercas();
}
