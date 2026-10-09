using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Pruebas.Comun;

/// <summary>Solicitud de costeo completa (formulario ampliado, HU-005.8) con dos posiciones de cubicaje, para las pruebas.</summary>
public static class clsSolicitudCosteoMuestra
{
    public static readonly DateTimeOffset Deadline = new(2026, 10, 15, 17, 0, 0, TimeSpan.FromHours(-5));

    public static clsSolicitudCosteo Crear() => new()
    {
        Tipo = TipoSolicitud.Spot,
        Deadline = Deadline,
        HorasCargue = 4m,
        HorasDescargue = 6.5m,
        Origen = "Buenaventura",
        Destino = "Bogotá D.C.",
        TipoVehiculo = "3S3",
        Carroceria = "PORTACONTENEDORES",
        Tipologia = "Contenedor",
        ClaseViaje = "Contenedor",
        ClaseContenedor = "CNT40.HC",
        Exclusivo = true,
        Consolidado = false,
        RetornaContenedor = true,
        CiudadDevolucionContenedor = "Buenaventura",
        LevanteContenedorDiferente = false,
        ValorAprovisionamiento = null,
        PesoKg = 28_500.5m,
        ValorMercancia = 250_000_000m,
        Posiciones =
        [
            new clsPosicionCarga("Estiba de café", 20, 120m, 100m, 150m, 1425m),
            new clsPosicionCarga(null, 1, 1200m, 235m, 269m, null),
        ],
    };

    /// <summary>Tipo de mercancía del catálogo del cubicador de las mercancías de <see cref="CrearCaptura"/>.</summary>
    public static readonly Guid TipoMercancia = Guid.Parse("6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50");

    /// <summary>
    /// La misma solicitud como captura nueva (HU-021.6): sin vehículo ni carrocería (decisión 7), con los ids de municipio del Nexo falso
    /// (Buenaventura 76109, Bogotá 11001) y las mercancías con su tipo del catálogo del cubicador.
    /// </summary>
    public static clsSolicitudCosteo CrearCaptura() => Crear() with
    {
        TipoVehiculo = null,
        Carroceria = null,
        OrigenMunicipioId = 76109,
        DestinoMunicipioId = 11001,
        Posiciones =
        [
            new clsPosicionCarga("Estiba de café", 20, 120m, 100m, 150m, 1425m, TipoMercancia),
            new clsPosicionCarga(null, 1, 1200m, 235m, 269m, null, TipoMercancia),
        ],
    };

    /// <summary>La primera ruta de <see cref="CrearCaptura"/> como línea de la captura, con la ciudad de devolución resuelta.</summary>
    public static clsLineaSolicitudCosteo RutaCaptura() => CrearCaptura().ALinea() with { CiudadDevolucionMunicipioId = 76109 };
}
