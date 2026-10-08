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
}
