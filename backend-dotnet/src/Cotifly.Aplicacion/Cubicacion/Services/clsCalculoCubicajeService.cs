using System.Globalization;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Cubicacion.Services;

/// <summary>
/// "Calcular cubicaje" contra el cubicador (HU-023.2, #157; ADR-020). Vista previa <b>sin persistencia</b>: no toca la base de datos ni
/// Nexo. Pasos:
/// <list type="number">
/// <item>Sin cubicador configurado: 503 <c>CUBICADOR_NO_DISPONIBLE</c> sin llamar a nadie.</item>
/// <item>Cada ítem viaja con su <c>codigoMercancia</c>; sin código, la descripción se resuelve al tipo activo más parecido del catálogo
/// (<see cref="clsSugeridorMercancia"/>, una lectura con caché). Sin coincidencia: 400 <c>MERCANCIA_DESCONOCIDA</c> con el ítem.</item>
/// <item><c>POST /recomendaciones</c> con <c>idLineaSolicitud = "PRUEBA-&lt;guid&gt;"</c>, <c>origenSistema = COTIFLY</c>, la clase de viaje
/// como preferencia y el <c>idUsuarioExterno</c> del comercial. El POST nunca se reintenta (lo garantiza el adaptador).</item>
/// <item>La respuesta se devuelve como una lista de vehículos con los nombres de los catálogos del cubicador (si no responden, los
/// códigos).</item>
/// </list>
/// Sin respaldo: una caída es 503 <c>CUBICADOR_NO_DISPONIBLE</c> con un mensaje para el comercial; el detalle técnico queda en el log.
/// </summary>
public sealed class clsCalculoCubicajeService(
    IClsCubicador cubicador,
    IClsUsuarioWebRepository usuarios,
    ILogger<clsCalculoCubicajeService> logger,
    IOptions<clsOpcionesCubicacion>? opciones = null) : IClsCalculoCubicajeService
{
    private readonly clsOpcionesCubicacion _opciones = opciones?.Value ?? new clsOpcionesCubicacion();

    /// <summary>Prefijo del <c>idLineaSolicitud</c> de la vista previa (decisión 2 de HU-023).</summary>
    public const string PrefijoPrueba = "PRUEBA-";

    public const string MensajeNoConfigurado =
        "No se puede calcular el cubicaje: el cubicador no está configurado en Cotifly. Avisa a soporte.";

    public const string MensajeNoDisponible =
        "No se pudo calcular el cubicaje: el cubicador no está disponible en este momento. Intenta de nuevo en unos minutos.";

    /// <summary>Códigos que llegan tal cual al comercial (rechazos del contrato, llave rechazada y respuesta inválida).</summary>
    private static readonly HashSet<string> _codigosPropios = new(StringComparer.Ordinal)
    {
        clsCodigosError.MercanciasRequeridas,
        clsCodigosError.MercanciaDesconocida,
        clsCodigosError.MercanciaNoCubicable,
        clsCodigosError.DimensionesInvalidas,
        clsCodigosError.CubicacionDemasiadoGrande,
        clsCodigosError.CubicadorNoAutorizado,
        clsCodigosError.CubicadorRespuestaInvalida,
    };

    public async Task<clsResultado<clsResultadoCubicacion>> CalcularAsync(
        Guid usuarioId, IReadOnlyList<clsItemCubicacion> items, string? claseViaje, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!cubicador.EstaConfigurado)
        {
            logger.LogWarning("Calcular cubicaje: Cubicador:UrlBase vacía; responde CUBICADOR_NO_DISPONIBLE.");
            return new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeNoConfigurado);
        }

        for (var i = 0; i < items.Count; i++)
        {
            // HU-023 (#176): el cubicador exige peso por unidad mayor que 0 en cada mercancía.
            if (items[i] is { } item && item.PesoKg is not > 0m)
            {
                return new clsErrorDominio(clsCodigosError.DimensionesInvalidas,
                    $"El ítem {(i + 1).ToString(CultureInfo.InvariantCulture)} debe traer el peso por unidad (peso_kg) mayor que 0.")
                {
                    Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["item"] = i },
                };
            }
        }

        var mercancias = await ResolverMercanciasAsync(items, cancellationToken);
        if (mercancias.EsFallo)
        {
            return mercancias.Error;
        }

        var idPrueba = PrefijoPrueba + Guid.NewGuid().ToString("D");
        var peticion = new clsPeticionRecomendacion(
            idPrueba,
            string.IsNullOrWhiteSpace(claseViaje) ? null : claseViaje.Trim(),
            await UsuarioExternoAsync(usuarioId, cancellationToken),
            [.. items.Select((item, i) => new clsMercanciaCubicador(
                mercancias.Valor[i].CodigoMercancia, item.Cantidad, item.LargoCm, item.AnchoCm, item.AltoCm, item.PesoKg))]);
        var recomendacion = await cubicador.RecomendarAsync(peticion, cancellationToken);
        if (recomendacion.EsFallo)
        {
            return Traducir(recomendacion.Error, idPrueba);
        }

        var respuesta = recomendacion.Valor;
        logger.LogInformation("Calcular cubicaje {IdPrueba}: {Resultado} {Motivo} con {Vehiculos} vehículo(s); no se guarda.",
            idPrueba, respuesta.Resultado, respuesta.MotivoSinSeleccion, respuesta.Vehiculos.Count);
        var vehiculos = respuesta.Asignado ? await VehiculosAsync(respuesta.Vehiculos, cancellationToken) : [];
        // Plan B del corte (Cubicacion:IncluirSugerenciaLegada, apagado por defecto): la forma anterior derivada del primer vehículo.
        var legada = _opciones.IncluirSugerenciaLegada ? clsSugerenciaVehiculoLegada.Desde(vehiculos) : null;
        return new clsResultadoCubicacion(
            respuesta.Resultado,
            respuesta.MotivoSinSeleccion,
            respuesta.Condicionada,
            vehiculos,
            respuesta.MercanciasCondicionadas,
            respuesta.Advertencias,
            respuesta.Extradimension,
            mercancias.Valor,
            legada)
        {
            MensajeSinSeleccion = respuesta.SinSeleccion ? clsMotivosSinSeleccion.Mensaje(respuesta.MotivoSinSeleccion) : null,
        };
    }

    /// <summary>
    /// El código con el que viaja cada ítem: el suyo o el del tipo más parecido a su descripción. El catálogo se lee solo si algún ítem
    /// no trae código.
    /// </summary>
    private async Task<clsResultado<IReadOnlyList<clsMercanciaCubicada>>> ResolverMercanciasAsync(
        IReadOnlyList<clsItemCubicacion> items, CancellationToken cancellationToken)
    {
        IReadOnlyList<clsTipoMercanciaCubicador>? catalogo = null;
        var resueltas = new List<clsMercanciaCubicada>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is null)
            {
                return SinMercancia(i, null);
            }

            if (!string.IsNullOrWhiteSpace(item.CodigoMercancia))
            {
                resueltas.Add(new clsMercanciaCubicada(item.CodigoMercancia.Trim(), null, Texto(item.Descripcion), Sugerida: false));
                continue;
            }

            // Cambio que rompe (documentado en CONTRATO_API_WEB §8.1 y §8.12): antes un ítem sin descripción se cubicaba; el cubicador
            // necesita el código, así que ahora es 400 MERCANCIA_DESCONOCIDA.
            if (string.IsNullOrWhiteSpace(item.Descripcion))
            {
                return SinMercancia(i, null);
            }

            if (catalogo is null)
            {
                var leido = await cubicador.ListarTiposMercanciaAsync(cancellationToken);
                if (leido.EsFallo)
                {
                    return Traducir(leido.Error, "catálogo de tipos de mercancía");
                }

                catalogo = leido.Valor;
            }

            var sugerida = clsSugeridorMercancia.Sugerir(
                item.Descripcion, catalogo, _opciones.ParecidoMinimoMercancia, _opciones.MargenAmbiguedadMercancia);
            if (sugerida is null)
            {
                return SinMercancia(i, item.Descripcion.Trim());
            }

            logger.LogInformation("Calcular cubicaje: la descripción del ítem {Item} se resolvió a {Codigo} (parecido {Parecido:0.00}).",
                i, sugerida.Tipo.Codigo, sugerida.Parecido);
            resueltas.Add(new clsMercanciaCubicada(sugerida.Tipo.Codigo, sugerida.Tipo.Nombre, item.Descripcion.Trim(), Sugerida: true));
        }

        return resueltas;
    }

    private static string? Texto(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    private static clsErrorDominio SinMercancia(int indice, string? descripcion)
    {
        var posicion = (indice + 1).ToString(CultureInfo.InvariantCulture);
        var mensaje = descripcion is null
            ? $"El ítem {posicion} no indica la mercancía: escoge el tipo de mercancía del catálogo."
            : $"No encontramos en el catálogo del cubicador una única mercancía parecida a «{descripcion}» (ítem {posicion}): escoge el tipo de mercancía del catálogo.";
        return new clsErrorDominio(clsCodigosError.MercanciaDesconocida, mensaje)
        {
            Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["item"] = indice, ["descripcion"] = descripcion },
        };
    }

    /// <summary>
    /// Los rechazos del contrato, la llave rechazada y la respuesta inválida llegan con su código y su mensaje; cualquier otra falla
    /// (caída, tiempo agotado, rechazo no previsto) es <c>CUBICADOR_NO_DISPONIBLE</c>. El detalle técnico solo va al log.
    /// </summary>
    private clsErrorDominio Traducir(clsErrorDominio error, string contexto)
    {
        logger.LogWarning("Calcular cubicaje ({Contexto}): el cubicador respondió {Codigo} ({Detalle}).", contexto, error.Codigo, error.Detalle);
        return _codigosPropios.Contains(error.Codigo)
            ? new clsErrorDominio(error.Codigo, error.Mensaje)
            : new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeNoDisponible);
    }

    private async Task<string?> UsuarioExternoAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        if (usuarioId == Guid.Empty)
        {
            return null;
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        return usuario?.IdUsuarioExterno?.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Los vehículos con los nombres de los catálogos del cubicador; un catálogo que no responde deja los códigos.</summary>
    private async Task<IReadOnlyList<clsVehiculoCubicacion>> VehiculosAsync(
        IReadOnlyList<clsVehiculoRecomendado> vehiculos, CancellationToken cancellationToken)
    {
        var tiposVehiculo = await cubicador.ListarTiposVehiculoAsync(cancellationToken);
        var tiposCarroceria = await cubicador.ListarTiposCarroceriaAsync(cancellationToken);
        if (tiposVehiculo.EsFallo || tiposCarroceria.EsFallo)
        {
            logger.LogInformation("Calcular cubicaje: el catálogo de vehículos o de carrocerías del cubicador no respondió; se muestran los códigos.");
        }

        var nombresVehiculo = Nombres(tiposVehiculo.EsExitoso ? tiposVehiculo.Valor.Select(t => (t.Codigo, t.Nombre)) : []);
        var nombresCarroceria = Nombres(tiposCarroceria.EsExitoso ? tiposCarroceria.Valor.Select(t => (t.Codigo, t.Nombre)) : []);
        return [.. vehiculos.Select(v => new clsVehiculoCubicacion(
            v.CodigoVehiculo,
            nombresVehiculo.GetValueOrDefault(v.CodigoVehiculo) ?? (string.IsNullOrWhiteSpace(v.NombreVehiculo) ? v.CodigoVehiculo : v.NombreVehiculo),
            v.CodigoCarroceria,
            nombresCarroceria.GetValueOrDefault(v.CodigoCarroceria) ?? v.CodigoCarroceria,
            v.PesoTotalKg,
            v.VolumenTotalM3,
            v.PorcentajeLlenadoVolumen,
            v.PorcentajeLlenadoPeso)
        {
            NumeroVehiculo = v.NumeroVehiculo,
            AlertaBajoLlenado = v.AlertaBajoLlenado,
            Heterogenea = v.Heterogenea,
            Mercancias = v.Mercancias ?? [],
        })];
    }

    private static Dictionary<string, string> Nombres(IEnumerable<(string Codigo, string Nombre)> filas)
    {
        var nombres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (codigo, nombre) in filas)
        {
            if (!string.IsNullOrWhiteSpace(codigo) && !string.IsNullOrWhiteSpace(nombre))
            {
                nombres.TryAdd(codigo, nombre);
            }
        }

        return nombres;
    }
}
