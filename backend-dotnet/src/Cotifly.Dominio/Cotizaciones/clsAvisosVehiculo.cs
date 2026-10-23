namespace Cotifly.Dominio.Cotizaciones;

/// <summary>Un vehículo en un aviso de la ruta (HU-032, #197): el tipo de vehículo y la carrocería (cualquiera puede faltar).</summary>
public sealed record clsVehiculoAviso(string? TipoVehiculo, string? Carroceria)
{
    /// <summary>El vehículo como lo lee el comercial: <c>3S3 - FURGON</c>, <c>3S3</c> o <c>sin vehículo</c>.</summary>
    public string ATexto() => (Limpio(TipoVehiculo), Limpio(Carroceria)) switch
    {
        (null, null) => "sin vehículo",
        ({ } tipo, null) => tipo,
        (null, { } carroceria) => carroceria,
        ({ } tipo, { } carroceria) => $"{tipo} - {carroceria}",
    };

    private static string? Limpio(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}

/// <summary>
/// HU-032 (#197, ADR-022): los avisos de la ruta sobre el vehículo que quedó en sus camiones. Viajan con las advertencias del cubicador de
/// la ruta (<see cref="clsAvisosCubicacion.Advertencias"/>, <c>avisosCubicacion</c>): se guardan, salen en la respuesta de la captura, de la
/// licitación (por fila), de la edición y en <c>GET .../costeo</c>. Son informativos: nunca bloquean.
/// </summary>
public static class clsAvisosVehiculo
{
    /// <summary>El cubicador asignó un vehículo distinto del elegido: manda el cubicador.</summary>
    public const string AjustadoPorCubicador = "VEHICULO_AJUSTADO_POR_CUBICADOR";

    /// <summary>El cubicador no seleccionó vehículo (o no respondió, o estaba ocupado): la ruta va con el vehículo elegido.</summary>
    public const string ElegidoUsado = "VEHICULO_ELEGIDO_USADO";

    /// <summary>La ruta mezcla mercancías cubicables y no cubicables: no se cubica y va con el vehículo elegido.</summary>
    public const string MercanciasNoCubicables = "MERCANCIAS_NO_CUBICABLES";

    /// <summary>
    /// El cubicador no seleccionó vehículo y el elegido no es un código de Nexo (catálogo o alias): la ruta va sin vehículo (lo resuelve la
    /// Mesa de Costeo) y la captura no se pierde.
    /// </summary>
    public const string ElegidoInvalido = "VEHICULO_ELEGIDO_INVALIDO";

    /// <summary>Los códigos de los avisos del vehículo (para reemplazarlos cuando la ruta se vuelve a planificar).</summary>
    public static readonly IReadOnlySet<string> Codigos = new HashSet<string>(StringComparer.Ordinal)
    {
        AjustadoPorCubicador, ElegidoUsado, MercanciasNoCubicables, ElegidoInvalido,
    };

    /// <summary><see cref="AjustadoPorCubicador"/>: con el vehículo elegido y los de los camiones del cubicador.</summary>
    public static clsAdvertenciaCubicador Ajustado(clsVehiculoAviso elegido, IReadOnlyList<clsVehiculoAviso> finales)
    {
        ArgumentNullException.ThrowIfNull(elegido);
        ArgumentNullException.ThrowIfNull(finales);
        return new clsAdvertenciaCubicador(AjustadoPorCubicador,
            $"El cubicador asignó {string.Join(", ", finales.Select(f => f.ATexto()))} en lugar del vehículo elegido ({elegido.ATexto()}): manda el cubicador.")
        {
            VehiculoElegido = elegido,
            VehiculosFinales = finales,
        };
    }

    /// <summary><see cref="ElegidoUsado"/>: el cubicador no escogió (<paramref name="causa"/>) y la ruta va con el elegido.</summary>
    public static clsAdvertenciaCubicador Usado(clsVehiculoAviso elegido, string causa)
    {
        ArgumentNullException.ThrowIfNull(elegido);
        ArgumentException.ThrowIfNullOrWhiteSpace(causa);
        return new clsAdvertenciaCubicador(ElegidoUsado, $"{causa}: la ruta va con el vehículo elegido ({elegido.ATexto()}).")
        {
            VehiculoElegido = elegido,
            VehiculosFinales = [elegido],
        };
    }

    /// <summary><see cref="MercanciasNoCubicables"/>: la ruta mixta no se cubica y va con el elegido (o sin vehículo, en Contenedor).</summary>
    public static clsAdvertenciaCubicador Mixta(clsVehiculoAviso elegido)
    {
        ArgumentNullException.ThrowIfNull(elegido);
        return new clsAdvertenciaCubicador(MercanciasNoCubicables,
            "La ruta tiene mercancías sin medidas ni tipo de mercancía: no se cubica y va con el vehículo elegido "
            + $"({elegido.ATexto()}). Las mercancías sin medidas no viajan a Nexo como posiciones: va el peso de la ruta.")
        {
            VehiculoElegido = elegido,
            VehiculosFinales = [elegido],
        };
    }

    /// <summary>
    /// <see cref="ElegidoInvalido"/>: el elegido no es (del todo) un código de Nexo; la ruta va con <paramref name="final"/> (sin vehículo si
    /// el tipo no lo es; sin carrocería si solo ella no lo es).
    /// </summary>
    public static clsAdvertenciaCubicador Invalido(clsVehiculoAviso elegido, clsVehiculoAviso final, string causa)
    {
        ArgumentNullException.ThrowIfNull(elegido);
        ArgumentNullException.ThrowIfNull(final);
        var resuelve = final.TipoVehiculo is null ? " y la resuelve la Mesa de Costeo" : string.Empty;
        return new clsAdvertenciaCubicador(ElegidoInvalido,
            $"{causa} y el vehículo elegido ({elegido.ATexto()}) no es un código de Nexo: la ruta va {(final.TipoVehiculo is null ? "sin vehículo" : $"con {final.ATexto()}")}{resuelve}.")
        {
            VehiculoElegido = elegido,
            VehiculosFinales = [final],
        };
    }

    /// <summary>
    /// Las advertencias de <paramref name="avisos"/> sin los avisos del vehículo, más <paramref name="nuevos"/>; <c>null</c> si no queda
    /// nada (ni advertencias, ni mercancías condicionadas, ni extradimensión).
    /// </summary>
    public static clsAvisosCubicacion? Combinar(clsAvisosCubicacion? avisos, IReadOnlyList<clsAdvertenciaCubicador>? nuevos)
    {
        var advertencias = (avisos?.Advertencias ?? []).Where(a => a.Codigo is null || !Codigos.Contains(a.Codigo)).Concat(nuevos ?? []).ToList();
        if (avisos is null && advertencias.Count == 0)
        {
            return null;
        }

        return new clsAvisosCubicacion(avisos?.MercanciasCondicionadas ?? [], advertencias, avisos?.Extradimension);
    }
}
