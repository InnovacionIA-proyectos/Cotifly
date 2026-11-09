using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Importación inteligente de NEXO en el Nexo falso (HU-044.1, #252, ADR-028), con la forma de CONTRATO.md §3.3 y §3.19.1 de NEXO:
/// <c>sugerir-mapeo</c>, <c>aplicar-mapeo</c>, <c>normalizar-valores</c>, <c>plantillas-mapeo</c>, <c>leer-estructura</c>,
/// <c>recetas-lectura</c> y <c>diccionario-cliente</c>. Simula la respuesta normal, la IA no disponible (<see cref="IaImportacionDisponible"/>:
/// 200 con <c>iaUsada = false</c> y <c>motivoSinIa</c>), un rechazo forzado (<see cref="ImportacionFalla"/> con
/// <see cref="CodigoImportacionFalla"/>: <c>429 IA_TOPE_DIARIO</c>, <c>400 RECETA_INVALIDA</c>...), la receta guardada por NIT y los 400 que
/// NEXO decide por su cuenta (receta que no es JSON, mapeo sin origen o destino, guardar sin cliente). El NIT se compara como en NEXO.
/// </summary>
public sealed partial class clsNexoFalso
{
    private const string RutaCaptura = "/api/captura-solicitudes/";

    private static readonly HashSet<string> _rutasImportacion = new(StringComparer.Ordinal)
    {
        "sugerir-mapeo", "aplicar-mapeo", "normalizar-valores", "plantillas-mapeo", "leer-estructura", "recetas-lectura",
    };

    private readonly List<clsPlantillaFalsa> _plantillas = [];
    private readonly List<clsRecetaFalsa> _recetas = [];

    /// <summary>¿La IA de NEXO responde? Con <c>false</c>, lo que la usa responde 200 con <c>iaUsada = false</c> y <c>IA_NO_DISPONIBLE</c>.</summary>
    public bool IaImportacionDisponible { get; set; } = true;

    /// <summary>Si tiene valor, toda ruta de la importación responde este estado con <see cref="CodigoImportacionFalla"/>.</summary>
    public HttpStatusCode? ImportacionFalla { get; set; }

    public string CodigoImportacionFalla { get; set; } = "IA_TOPE_DIARIO";

    /// <summary>Mensaje del rechazo forzado (el que Cotifly debe reenviar tal cual).</summary>
    public string MensajeImportacionFalla { get; set; } = "Rechazo de la importación (prueba).";

    /// <summary>NIT (normalizados) que no son de ningún cliente ni prospecto: guardar con ellos da <c>400 CLIENTE_NIT_INVALIDO</c>.</summary>
    public HashSet<string> NitsDesconocidos { get; } = new(StringComparer.Ordinal);

    /// <summary>Términos del diccionario de NEXO (<c>ClienteId</c> null = generales).</summary>
    public List<(Guid? ClienteId, string Termino, string Tipo, string Concepto, string? Contexto)> Diccionario { get; } =
        [(null, "Tractomula", "valor", "3S3", "tipoVehiculo"), (null, "ORIGEN", "columna", "origen", null)];

    /// <summary>Campos del último <c>multipart</c> recibido (sin el archivo); el archivo, en <c>archivo</c> con su nombre.</summary>
    public IReadOnlyDictionary<string, string>? UltimoFormularioImportacion { get; private set; }

    /// <summary>Recetas guardadas, como JSON, por NIT normalizado (en el orden de guardado).</summary>
    public IReadOnlyList<(string Nit, string Receta)> RecetasGuardadas
    {
        get
        {
            lock (_candado)
            {
                return [.. _recetas.Select(r => (r.Nit, r.Receta.ToJsonString()))];
            }
        }
    }

    /// <summary>Receta de lectura por defecto que propone la "IA" del falso.</summary>
    public static string RecetaDePrueba => """{"version":1,"hojas":[{"hoja":"*","usar":true,"trasponer":false,"filasEncabezado":[3],"hastaPrimeraFilaVacia":true,"etiquetas":[{"columna":"A","nombre":"Origen"}]}],"unir":false,"notas":null}""";

    /// <summary>El NIT como lo compara NEXO (<c>clsNit.Normalizar</c>).</summary>
    public static string? NitComoNexo(string? nit)
    {
        if (string.IsNullOrWhiteSpace(nit))
        {
            return null;
        }

        var limpio = new string([.. nit.Trim().Split('-')[0].Where(char.IsLetterOrDigit)]);
        return limpio.Length == 0 ? null : limpio.ToUpperInvariant();
    }

    private static bool EsRutaImportacion(string ruta) =>
        ruta.StartsWith("/api/diccionario-cliente", StringComparison.Ordinal)
        || (ruta.StartsWith(RutaCaptura, StringComparison.Ordinal) && _rutasImportacion.Contains(ruta[RutaCaptura.Length..].Split('/')[0]));

    private static async Task<Dictionary<string, string>?> LeerFormularioAsync(HttpContent contenido, CancellationToken cancellationToken)
    {
        if (contenido is not MultipartFormDataContent multipart)
        {
            return null;
        }

        var campos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parte in multipart)
        {
            var nombre = parte.Headers.ContentDisposition?.Name?.Trim('"');
            if (nombre is null)
            {
                continue;
            }

            campos[nombre] = nombre == "archivo"
                ? parte.Headers.ContentDisposition!.FileName?.Trim('"') ?? string.Empty
                : await parte.ReadAsStringAsync(cancellationToken);
        }

        return campos;
    }

    private HttpResponseMessage Importacion(string metodo, string ruta, Uri url, string? cuerpo, Dictionary<string, string>? formulario)
    {
        if (formulario is not null)
        {
            UltimoFormularioImportacion = formulario;
        }

        if (ImportacionFalla is { } falla)
        {
            return Error(falla, CodigoImportacionFalla, MensajeImportacionFalla);
        }

        var json = cuerpo is null ? null : JsonNode.Parse(cuerpo);
        var consulta = System.Web.HttpUtility.ParseQueryString(url.Query);
        var accion = ruta.StartsWith(RutaCaptura, StringComparison.Ordinal) ? ruta[RutaCaptura.Length..] : ruta;
        return (metodo, accion) switch
        {
            ("POST", "sugerir-mapeo") => SugerirMapeo(json),
            ("POST", "aplicar-mapeo") => AplicarMapeo(json),
            ("POST", "normalizar-valores") => NormalizarValores(json),
            ("PUT", "plantillas-mapeo") => GuardarPlantilla(json),
            ("GET", "plantillas-mapeo") => Json(HttpStatusCode.OK, _plantillas
                .Where(p => NitComoNexo(consulta["clienteNit"]) is not { } nit || p.Nit == nit).Select(p => p.Dto()).ToList()),
            ("DELETE", var r) when r.StartsWith("plantillas-mapeo/", StringComparison.Ordinal) =>
                _plantillas.RemoveAll(p => p.Id.ToString() == r["plantillas-mapeo/".Length..]) > 0
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : Error(HttpStatusCode.NotFound, "PLANTILLA_NO_ENCONTRADA", "La plantilla no existe."),
            ("POST", "leer-estructura") => LeerEstructura(formulario ?? new Dictionary<string, string>()),
            ("PUT", "recetas-lectura") => GuardarReceta(formulario ?? new Dictionary<string, string>()),
            ("GET", "recetas-lectura") => Json(HttpStatusCode.OK, _recetas
                .Where(r => NitComoNexo(consulta["clienteNit"]) is not { } nit || r.Nit == nit).Select(r => r.Dto()).ToList()),
            ("DELETE", var r) when r.StartsWith("recetas-lectura/", StringComparison.Ordinal) =>
                _recetas.RemoveAll(x => x.Id.ToString() == r["recetas-lectura/".Length..]) > 0
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : Error(HttpStatusCode.NotFound, "RECETA_NO_ENCONTRADA", "El formato no existe."),
            ("GET", "/api/diccionario-cliente/opciones") => Json(HttpStatusCode.OK, new
            {
                tipos = new[] { "columna", "unidad", "valor" },
                camposDestino = new[] { "origen", "destino", "peso", "tipoVehiculo", "ignorar" },
                unidades = new[] { "kg", "t" },
                contextos = new[] { "tipoVehiculo", "carroceria", "lineaNegocio" },
            }),
            ("GET", "/api/diccionario-cliente") => consulta["tipo"] is { } tipo && tipo is not ("columna" or "unidad" or "valor")
                ? Error(HttpStatusCode.BadRequest, "TIPO_TERMINO_INVALIDO", "El tipo de término no es válido.")
                : Json(HttpStatusCode.OK, Diccionario.Select(t => new
                {
                    id = Guid.NewGuid(), clienteId = t.ClienteId, clienteNombre = (string?)null, termino = t.Termino, tipo = t.Tipo,
                    concepto = t.Concepto, contexto = t.Contexto, ejemplo = (string?)null, activo = true, createdAt = DateTimeOffset.UtcNow,
                    updatedAt = (DateTimeOffset?)null,
                }).ToList()),
            _ => Error(HttpStatusCode.NotFound, "RUTA_NO_ENCONTRADA", $"{metodo} {ruta}"),
        };
    }

    private HttpResponseMessage SugerirMapeo(JsonNode? json)
    {
        var encabezados = json?["encabezados"]?.AsArray().Select(e => e?.GetValue<string>() ?? string.Empty).ToList() ?? [];
        if (encabezados.Count == 0)
        {
            return Error(HttpStatusCode.BadRequest, "ENCABEZADOS_REQUERIDOS", "Envíe los encabezados del archivo.");
        }

        if (encabezados.Count > 100)
        {
            return Error(HttpStatusCode.BadRequest, "DEMASIADAS_COLUMNAS", "El archivo tiene más de 100 columnas.");
        }

        var nit = NitComoNexo(json?["clienteNit"]?.GetValue<string>());
        var plantilla = _plantillas.FirstOrDefault(p => p.Nit == nit && p.Encabezados.Select(NormalizarComoNexo).SequenceEqual(encabezados.Select(NormalizarComoNexo)));
        if (plantilla is not null)
        {
            return Json(HttpStatusCode.OK, new
            {
                iaUsada = false, motivoSinIa = "PLANTILLA_RECONOCIDA", dudas = Array.Empty<string>(), plantillaId = plantilla.Id,
                columnas = plantilla.Columnas.Select(c => new
                {
                    indice = c.Indice, columna = encabezados[c.Indice], campo = c.Campo, transformacion = c.Transformacion, confianza = 1.0,
                    origen = "plantilla", nota = (string?)null,
                }).ToList(),
            });
        }

        return Json(HttpStatusCode.OK, new
        {
            iaUsada = IaImportacionDisponible,
            motivoSinIa = IaImportacionDisponible ? null : "IA_NO_DISPONIBLE",
            dudas = IaImportacionDisponible ? new[] { "¿TON es el peso total?" } : [],
            plantillaId = (Guid?)null,
            columnas = encabezados.Select((e, i) =>
            {
                var campo = CampoSugerido(e);
                var conIa = IaImportacionDisponible && campo != "ignorar";
                return new
                {
                    indice = i, columna = e, campo = conIa ? campo : "ignorar",
                    transformacion = conIa && campo == "peso" ? "peso_toneladas_a_kg" : "ninguna",
                    confianza = conIa ? 0.9 : 0.0, origen = conIa ? "ia" : "ninguno", nota = (string?)null,
                };
            }).ToList(),
        });
    }

    private static string CampoSugerido(string encabezado) => NormalizarComoNexo(encabezado) switch
    {
        "ORIGEN" => "origen",
        "DESTINO" => "destino",
        "TON" or "PESO" => "peso",
        _ => "ignorar",
    };

    private static HttpResponseMessage AplicarMapeo(JsonNode? json)
    {
        var columnas = json?["columnas"]?.AsArray()
            .Select(c => (Indice: c!["indice"]!.GetValue<int>(), Campo: c["campo"]!.GetValue<string>(), Transformacion: c["transformacion"]?.GetValue<string>()))
            .ToList() ?? [];
        if (columnas.Count == 0)
        {
            return Error(HttpStatusCode.BadRequest, "MAPEO_REQUERIDO", "Envíe el mapeo de columnas.");
        }

        if (columnas.GroupBy(c => c.Campo).Any(g => g.Key != "ignorar" && g.Count() > 1))
        {
            return Error(HttpStatusCode.BadRequest, "CAMPO_DUPLICADO", "Dos columnas apuntan al mismo campo.");
        }

        var origen = columnas.FirstOrDefault(c => c.Campo == "origen");
        var destino = columnas.FirstOrDefault(c => c.Campo == "destino");
        if (origen.Campo is null || destino.Campo is null)
        {
            return Error(HttpStatusCode.BadRequest, "CAMPO_REQUERIDO", "El mapeo debe tener una columna de origen y una de destino.");
        }

        var peso = columnas.FirstOrDefault(c => c.Campo == "peso");
        var filas = json?["filas"]?.AsArray().Select(f => f?.AsArray().Select(c => c?.GetValue<string>() ?? string.Empty).ToList() ?? []).ToList() ?? [];
        var primeraFila = json?["primeraFila"]?.GetValue<int>() ?? 2;
        var lineas = new List<object>();
        var errores = new List<object>();
        for (var i = 0; i < filas.Count; i++)
        {
            string Celda(int indice) => indice < filas[i].Count ? filas[i][indice] : string.Empty;
            if (string.IsNullOrWhiteSpace(Celda(origen.Indice)) || string.IsNullOrWhiteSpace(Celda(destino.Indice)))
            {
                errores.Add(new { fila = primeraFila + i, campo = "origen", codigo = "LINEA_INCOMPLETA", mensaje = "Falta el origen o el destino." });
                continue;
            }

            decimal? kilos = peso.Campo is not null && decimal.TryParse(Celda(peso.Indice), System.Globalization.CultureInfo.InvariantCulture, out var valor)
                ? peso.Transformacion == "peso_toneladas_a_kg" ? valor * 1000 : valor
                : null;
            lineas.Add(new { fila = primeraFila + i, origen = Celda(origen.Indice), destino = Celda(destino.Indice), peso = kilos });
        }

        return Json(HttpStatusCode.OK, new
        {
            totalFilas = filas.Count, filasVacias = 0, filasConErrores = errores.Count, lineas, errores, totalErrores = errores.Count,
        });
    }

    private HttpResponseMessage NormalizarValores(JsonNode? json)
    {
        var valores = json?["valores"]?.AsArray()
            .Select(v => (Campo: v!["campo"]!.GetValue<string>(), Valor: v["valor"]?.GetValue<string>() ?? string.Empty)).ToList() ?? [];
        if (valores.Count == 0)
        {
            return Error(HttpStatusCode.BadRequest, "VALORES_REQUERIDOS", "Envíe los valores a normalizar.");
        }

        if (valores.FirstOrDefault(v => v.Campo is not ("tipoVehiculo" or "carroceria" or "lineaNegocio" or "ciudad")) is { Campo: not null } invalido)
        {
            return Error(HttpStatusCode.BadRequest, "CAMPO_INVALIDO", $"El campo '{invalido.Campo}' no se normaliza.");
        }

        var resultado = valores.DistinctBy(v => (v.Campo, NormalizarComoNexo(v.Valor))).Select(v =>
        {
            var alias = Diccionario.FirstOrDefault(t => t.Tipo == "valor" && t.Contexto == v.Campo && NormalizarComoNexo(t.Termino) == NormalizarComoNexo(v.Valor));
            var resuelto = alias.Termino is not null || IaImportacionDisponible;
            return new
            {
                campo = v.Campo, valor = v.Valor, estado = resuelto ? "resuelto" : "sin_resolver",
                codigo = alias.Termino is not null ? alias.Concepto : resuelto ? NormalizarComoNexo(v.Valor) : null,
                descripcion = (string?)null, municipioId = (int?)null,
                origen = alias.Termino is not null ? "diccionario" : resuelto ? "ia" : "ninguno",
                confianza = alias.Termino is not null ? 1.0 : resuelto ? 0.8 : 0.0, nota = (string?)null, candidatos = (object[]?)null,
            };
        }).ToList();
        var todosPorRegla = resultado.All(r => r.origen == "diccionario");
        return Json(HttpStatusCode.OK, new
        {
            iaUsada = IaImportacionDisponible && !todosPorRegla,
            motivoSinIa = todosPorRegla ? "SIN_PENDIENTES" : IaImportacionDisponible ? null : "IA_NO_DISPONIBLE",
            totalDistintos = resultado.Count,
            valores = resultado,
        });
    }

    private HttpResponseMessage GuardarPlantilla(JsonNode? json)
    {
        if (NitComoNexo(json?["clienteNit"]?.GetValue<string>()) is not { } nit)
        {
            return Error(HttpStatusCode.BadRequest, "CLIENTE_REQUERIDO", "Indique el cliente.");
        }

        if (NitsDesconocidos.Contains(nit))
        {
            return Error(HttpStatusCode.BadRequest, "CLIENTE_NIT_INVALIDO", $"El NIT '{nit}' no corresponde a ningún cliente ni prospecto.");
        }

        var encabezados = json?["encabezados"]?.AsArray().Select(e => e?.GetValue<string>() ?? string.Empty).ToList() ?? [];
        var columnas = json?["columnas"]?.AsArray()
            .Select(c => new clsColumnaFalsa(c!["indice"]!.GetValue<int>(), c["campo"]!.GetValue<string>(), c["transformacion"]?.GetValue<string>() ?? "ninguna"))
            .ToList() ?? [];
        var anterior = _plantillas.FindIndex(p => p.Nit == nit && p.Encabezados.Select(NormalizarComoNexo).SequenceEqual(encabezados.Select(NormalizarComoNexo)));
        var plantilla = new clsPlantillaFalsa(anterior >= 0 ? _plantillas[anterior].Id : Guid.NewGuid(), nit, json?["nombre"]?.GetValue<string>(), encabezados, columnas);
        if (anterior >= 0)
        {
            _plantillas[anterior] = plantilla;
        }
        else
        {
            _plantillas.Add(plantilla);
        }

        return Json(anterior >= 0 ? HttpStatusCode.OK : HttpStatusCode.Created, plantilla.Dto());
    }

    private HttpResponseMessage LeerEstructura(Dictionary<string, string> formulario)
    {
        if (!formulario.TryGetValue("archivo", out var archivo))
        {
            return Error(HttpStatusCode.BadRequest, "ARCHIVO_REQUERIDO", "Adjunte el archivo.");
        }

        var instrucciones = formulario.GetValueOrDefault("instrucciones");
        JsonNode? recetaEnviada = null;
        if (formulario.TryGetValue("receta", out var textoReceta) && !string.IsNullOrWhiteSpace(textoReceta))
        {
            if (Receta(textoReceta) is not { } leida)
            {
                return Error(HttpStatusCode.BadRequest, "RECETA_INVALIDA", "La receta no es un JSON válido.");
            }

            recetaEnviada = leida;
        }

        var nit = NitComoNexo(formulario.GetValueOrDefault("clienteNit"));
        var guardada = nit is null ? null : _recetas.LastOrDefault(r => r.Nit == nit);
        (string Origen, bool Ia, string? Motivo, JsonNode Receta, Guid? Guardada) lectura =
            recetaEnviada is not null && string.IsNullOrWhiteSpace(instrucciones) ? ("manual", false, "SIN_INSTRUCCION", recetaEnviada!, (Guid?)null)
            : recetaEnviada is null && string.IsNullOrWhiteSpace(instrucciones) && guardada is not null
                ? ("guardada", false, "RECETA_GUARDADA", guardada.Receta.DeepClone(), (Guid?)guardada.Id)
            : IaImportacionDisponible ? ("ia", true, (string?)null, recetaEnviada ?? JsonNode.Parse(RecetaDePrueba)!, (Guid?)null)
            : ("automatica", false, "IA_NO_DISPONIBLE", JsonNode.Parse(RecetaDePrueba)!, (Guid?)null);
        if (guardada is not null)
        {
            guardada.Usos += lectura.Origen == "guardada" ? 1 : 0;
        }

        return Json(HttpStatusCode.OK, new
        {
            archivo,
            origenReceta = lectura.Origen,
            similitud = lectura.Guardada is null ? (double?)null : 1.0,
            recetaGuardadaId = lectura.Guardada,
            iaUsada = lectura.Ia,
            motivoSinIa = lectura.Motivo,
            receta = lectura.Receta,
            resumen = lectura.Ia ? "La hoja es una tabla cruzada con los orígenes en las columnas." : null,
            dudas = lectura.Ia ? new[] { "¿Las celdas vacías son rutas a cotizar?" } : [],
            hojasDelArchivo = new[] { new { nombre = "Pliego", oculta = false, filas = 12, columnas = 4, usada = true } },
            tablas = new[]
            {
                new
                {
                    nombre = "Pliego", filaEncabezado = 3, encabezados = new[] { "ORIGEN", "DESTINO", "TON" },
                    filas = new[] { new[] { "Buenaventura", "Bogotá", "32" }, new[] { "Cartagena", "Medellín", "28" } }, totalFilas = 2,
                },
            },
            avisos = lectura.Ia ? Array.Empty<string>() : ["Se leyó sin IA."],
            cambios = (object?)null,
        });
    }

    private HttpResponseMessage GuardarReceta(Dictionary<string, string> formulario)
    {
        if (!formulario.ContainsKey("archivo"))
        {
            return Error(HttpStatusCode.BadRequest, "ARCHIVO_REQUERIDO", "Adjunte el archivo.");
        }

        if (!formulario.TryGetValue("receta", out var textoReceta) || string.IsNullOrWhiteSpace(textoReceta))
        {
            return Error(HttpStatusCode.BadRequest, "RECETA_REQUERIDA", "Envíe la receta.");
        }

        if (Receta(textoReceta) is not { } receta)
        {
            return Error(HttpStatusCode.BadRequest, "RECETA_INVALIDA", "La receta no es un JSON válido.");
        }

        if (NitComoNexo(formulario.GetValueOrDefault("clienteNit")) is not { } nit)
        {
            return Error(HttpStatusCode.BadRequest, "CLIENTE_REQUERIDO", "Indique el cliente.");
        }

        if (NitsDesconocidos.Contains(nit))
        {
            return Error(HttpStatusCode.BadRequest, "CLIENTE_NIT_INVALIDO", $"El NIT '{nit}' no corresponde a ningún cliente ni prospecto.");
        }

        // Como NEXO con un formato ≥ 85 % parecido: el falso reemplaza el que ya tenga el cliente (200) y si no, crea (201).
        var anterior = _recetas.FindIndex(r => r.Nit == nit);
        var nueva = new clsRecetaFalsa(anterior >= 0 ? _recetas[anterior].Id : Guid.NewGuid(), nit, formulario.GetValueOrDefault("nombre"), receta,
            formulario.GetValueOrDefault("instrucciones"));
        if (anterior >= 0)
        {
            _recetas[anterior] = nueva;
        }
        else
        {
            _recetas.Add(nueva);
        }

        return Json(anterior >= 0 ? HttpStatusCode.OK : HttpStatusCode.Created, nueva.Dto());
    }

    /// <summary>La receta como objeto JSON, o <c>null</c> si no lo es (NEXO: <c>RECETA_INVALIDA</c>).</summary>
    private static JsonNode? Receta(string texto)
    {
        try
        {
            return JsonNode.Parse(texto) is JsonObject objeto ? objeto : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record clsColumnaFalsa(int Indice, string Campo, string Transformacion);

    private sealed record clsPlantillaFalsa(Guid Id, string Nit, string? Nombre, List<string> Encabezados, List<clsColumnaFalsa> Columnas)
    {
        public Guid ClienteId { get; } = DeterministicoPorNit(Nit);

        public object Dto() => new
        {
            id = Id, clienteId = ClienteId, clienteNombre = "Cliente " + Nit, nombre = Nombre, encabezados = Encabezados,
            columnas = Columnas.Select(c => new { indice = c.Indice, campo = c.Campo, transformacion = c.Transformacion }).ToList(),
            createdAt = DateTimeOffset.UtcNow, updatedAt = (DateTimeOffset?)null,
        };
    }

    private sealed class clsRecetaFalsa(Guid id, string nit, string? nombre, JsonNode receta, string? instrucciones)
    {
        public Guid Id { get; } = id;

        public string Nit { get; } = nit;

        public JsonNode Receta { get; } = receta;

        public int Usos { get; set; }

        public object Dto() => new
        {
            id = Id, clienteId = DeterministicoPorNit(Nit), clienteNombre = "Cliente " + Nit, nombre, receta = Receta.DeepClone(), instrucciones,
            usos = Usos, ultimoUsoAt = (DateTimeOffset?)null, createdAt = DateTimeOffset.UtcNow, updatedAt = (DateTimeOffset?)null,
        };
    }

    /// <summary>El id local del cliente en NEXO, estable por NIT (como el espejo local de NEXO).</summary>
    private static Guid DeterministicoPorNit(string nit)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(nit));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
