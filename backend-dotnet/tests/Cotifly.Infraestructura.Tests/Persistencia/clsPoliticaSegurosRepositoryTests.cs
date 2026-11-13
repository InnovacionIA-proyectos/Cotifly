using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Seguros;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Infraestructura.Cotizaciones.Repositories;
using Cotifly.Pruebas.Comun;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// <see cref="clsPoliticaSegurosRepository"/> sobre las tablas reales del script 21 (con la carga aprobada que siembra el
/// mismo script). Los cambios se hacen en una base AISLADA: la compartida conserva la política aprobada para las demás pruebas.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsPoliticaSegurosRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private const decimal M = 1_000_000m;

    private static clsCotiflyDbContext Contexto(string cadena) =>
        new(new DbContextOptionsBuilder<clsCotiflyDbContext>().UseSqlServer(cadena).Options);

    [Fact]
    public async Task LeeLaPoliticaAprobadaDelScript21()
    {
        await using var contexto = Contexto(BaseDatos.CadenaCotifly);

        var politica = await new clsPoliticaSegurosRepository(contexto).ObtenerAsync(Ct);

        politica.Tramos.Select(t => t.Nombre).Should().Equal(
            "≤ 150 M", "> 150 – 250 M", "> 250 – 350 M", "> 350 – 500 M", "> 500 – 700 M", "> 700 – 1.000 M", "> 1.000 M");
        politica.Tramos.Sum(t => t.Medidas.Count).Should().Be(25, "las 25 reglas aprobadas");
        politica.Tramos[^1].ValorHasta.Should().BeNull("el último tramo no tiene tope");
        politica.TramoDe(250 * M)!.Nombre.Should().Be("> 150 – 250 M");
        var vehicular = politica.Tramos[3].Medidas.Single(m => m.EsObligatoria);
        vehicular.Codigo.Should().Be("acompanante_vehicular");
        vehicular.Nombre.Should().Be("Acompañante vehicular (parcial o total)");
        vehicular.Conexos.Should().Equal("AVP_MOTO", "AVP_MOTO_ARMADA", "AVT_AUTO", "AVT_MOTO", "AVT_MOTO_ARMADA");
        politica.Tramos[5].Medidas.Single(m => m.EsObligatoria).Conexos.Should().Equal("AVT_AUTO", "AVT_MOTO", "AVT_MOTO_ARMADA");
        politica.Evaluar(400 * M, incluyeSeguro: false, ["AVT_AUTO"]).Pendientes.Should().BeEmpty("un acompañante total cumple en 350 – 500 M");
        politica.Evaluar(400 * M, incluyeSeguro: false, []).Pendientes.Should().ContainSingle();
    }

    [Fact]
    public async Task UnCambioEnLasTablasSeVeEnLaSiguienteLecturaSinDesplegar()
    {
        await using var aislada = await BaseDatos.CrearBaseConScriptsAsync("dbPoliticaCambio", clsBaseDatosPruebas.ScriptsSin07);
        await using var contexto = Contexto(aislada.Cadena);
        var repositorio = new clsPoliticaSegurosRepository(contexto);
        (await repositorio.ObtenerAsync(Ct)).Evaluar(300 * M, incluyeSeguro: false, []).Obligatorias.Should().BeEmpty();

        // Datos vuelve obligatorio el acompañante desde 250 M (como en cambiar_politica_seguros.sql), con la API arriba.
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, """
            INSERT INTO dbo_cotifly.PoliticaSeguroTramoMedidas (tramoId, medidaId, nivel)
            SELECT t.id, m.id, 'obligatoria' FROM dbo_cotifly.PoliticaSeguroTramos t, dbo_cotifly.PoliticaSeguroMedidas m
            WHERE t.nombre = N'> 250 – 350 M' AND m.codigo = N'acompanante_vehicular';
            UPDATE dbo_cotifly.PoliticaSeguroTramoMedidas SET nivel = 'obligatoria'
            WHERE medidaId = (SELECT id FROM dbo_cotifly.PoliticaSeguroMedidas WHERE codigo = N'gps');
            """);

        var cambiada = (await repositorio.ObtenerAsync(Ct)).Evaluar(300 * M, incluyeSeguro: false, []);

        cambiada.Pendientes.Select(m => m.Codigo).Should().Equal(["acompanante_vehicular"], "la misma instancia lee de nuevo: sin caché");
        cambiada.Obligatorias.Single(m => m.Codigo == "gps").Estado.Should().Be("sin_verificar", "una obligatoria sin conexos queda sin_verificar y nunca pendiente");
    }

    [Fact]
    public async Task SoloCuentanLosTramosYLasMedidasActivos()
    {
        await using var aislada = await BaseDatos.CrearBaseConScriptsAsync("dbPoliticaActivos", clsBaseDatosPruebas.ScriptsSin07);
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, """
            UPDATE dbo_cotifly.PoliticaSeguroMedidas SET isActive = 0 WHERE codigo = N'acompanante_vehicular';
            UPDATE dbo_cotifly.PoliticaSeguroTramos SET isActive = 0 WHERE nombre = N'> 1.000 M';
            """);
        await using var contexto = Contexto(aislada.Cadena);

        var politica = await new clsPoliticaSegurosRepository(contexto).ObtenerAsync(Ct);

        politica.Tramos.Should().HaveCount(6);
        politica.Evaluar(400 * M, incluyeSeguro: false, []).Obligatorias.Should().BeEmpty("la medida inactiva no aplica");
        politica.Evaluar(2_000 * M, incluyeSeguro: false, []).Aviso.Should().BeNull("sin tramo activo para el valor no hay aviso de contrato");
    }

    [Fact]
    public async Task SinTramosActivosLaPoliticaEstaVacia()
    {
        await using var aislada = await BaseDatos.CrearBaseConScriptsAsync("dbPoliticaVacia", clsBaseDatosPruebas.ScriptsSin07);
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, "UPDATE dbo_cotifly.PoliticaSeguroTramos SET isActive = 0");
        await using var contexto = Contexto(aislada.Cadena);

        (await new clsPoliticaSegurosRepository(contexto).ObtenerAsync(Ct)).Should().BeSameAs(clsPoliticaSeguros.Vacia);
    }

    [Fact]
    public async Task CotizacionServiciosConexosTieneElCodigoDelServicioNullable()
    {
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            "SELECT CONCAT(DATA_TYPE, '|', CHARACTER_MAXIMUM_LENGTH, '|', IS_NULLABLE) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_NAME = 'CotizacionServiciosConexos' AND COLUMN_NAME = 'codigoServicio'"))
            .Should().Be("nvarchar|40|YES");
    }

    [Fact]
    public async Task GuardarElCodigoDeUnaFilaQueOtraPeticionBorroNoFallaYNoTocaNada()
    {
        // B2 de la auditoría: el GET del costeo guarda fila por fila con WHERE codigoServicio IS NULL; una fila borrada en paralelo da 0.
        var cotizacion = clsCotizacionMuestra.ConConexo(clsCotizacionMuestra.Costeada(), "FILA-BORRADA", "Acompañante", 100_000m);
        cotizacion.CompletarCodigosConexos(_ => "AVT_AUTO").Should().ContainSingle();
        await using var ambito = NuevoAmbito();

        var guardadas = await ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>()
            .GuardarCodigosServicioAsync([.. cotizacion.PrimeraRuta!.Conexos], Ct);

        guardadas.Should().Be(0);
    }
}
