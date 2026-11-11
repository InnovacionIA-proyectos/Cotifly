using Cotifly.Dominio.Seguros;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// HU-046.3 (#269): la carga inicial aprobada de la política de seguros (HU-046.1 #267, la que siembra <c>db/scripts/21_politica_seguros.sql</c>)
/// como objeto del dominio, para las pruebas sin base de datos. Las de integración leen la de verdad del script 21.
/// </summary>
public static class clsPoliticaSegurosMuestra
{
    public const decimal Millon = 1_000_000m;

    /// <summary>Los 5 acompañantes que cumplen <c>acompanante_vehicular</c> (decisión 11).</summary>
    public static readonly string[] Acompanantes = ["AVP_MOTO", "AVP_MOTO_ARMADA", "AVT_AUTO", "AVT_MOTO", "AVT_MOTO_ARMADA"];

    /// <summary>Los 3 acompañantes totales que cumplen <c>acompanante_total</c>.</summary>
    public static readonly string[] AcompanantesTotales = ["AVT_AUTO", "AVT_MOTO", "AVT_MOTO_ARMADA"];

    private static clsMedidaTramoSeguro R(string codigo) => new(codigo, Nombre(codigo), EsCobrable: false, clsNivelMedidaSeguro.Recomendada, []);

    private static string Nombre(string codigo) => codigo switch
    {
        "esquema_basico" => "Esquema básico de seguridad",
        "gps" => "GPS",
        "candado_satelital" => "Candado satelital",
        "restriccion_nocturna" => "Restricción de tránsito nocturno",
        "acompanante_vehicular" => "Acompañante vehicular (parcial o total)",
        "acompanante_total" => "Acompañante vehicular total",
        "incluir_seguro" => "Incluir el seguro de la mercancía",
        _ => codigo,
    };

    private static readonly clsMedidaTramoSeguro _vehicular =
        new("acompanante_vehicular", Nombre("acompanante_vehicular"), EsCobrable: true, clsNivelMedidaSeguro.Obligatoria, Acompanantes);

    private static readonly clsMedidaTramoSeguro _total =
        new("acompanante_total", Nombre("acompanante_total"), EsCobrable: true, clsNivelMedidaSeguro.Obligatoria, AcompanantesTotales);

    /// <summary>La política aprobada: 7 tramos, 25 reglas, 8 conexos.</summary>
    public static clsPoliticaSeguros Aprobada() => new(
    [
        new(Guid.NewGuid(), "≤ 150 M", 0m, 150 * Millon, [R("esquema_basico")]),
        new(Guid.NewGuid(), "> 150 – 250 M", 150 * Millon, 250 * Millon, [R("esquema_basico"), R("candado_satelital")]),
        new(Guid.NewGuid(), "> 250 – 350 M", 250 * Millon, 350 * Millon,
            [R("esquema_basico"), R("gps"), R("candado_satelital"), R("incluir_seguro")]),
        new(Guid.NewGuid(), "> 350 – 500 M", 350 * Millon, 500 * Millon,
            [R("esquema_basico"), R("gps"), R("candado_satelital"), R("restriccion_nocturna"), _vehicular, R("incluir_seguro")]),
        new(Guid.NewGuid(), "> 500 – 700 M", 500 * Millon, 700 * Millon,
            [R("esquema_basico"), R("gps"), R("candado_satelital"), R("restriccion_nocturna"), _vehicular, R("incluir_seguro")]),
        new(Guid.NewGuid(), "> 700 – 1.000 M", 700 * Millon, 1_000 * Millon,
            [R("esquema_basico"), R("gps"), R("candado_satelital"), R("restriccion_nocturna"), _total, R("incluir_seguro")]),
        new(Guid.NewGuid(), "> 1.000 M", 1_000 * Millon, null, []),
    ]);
}
