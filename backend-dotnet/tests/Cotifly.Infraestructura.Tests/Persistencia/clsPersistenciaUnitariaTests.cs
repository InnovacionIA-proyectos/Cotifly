using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>Piezas de persistencia que no necesitan base de datos.</summary>
public sealed class clsPersistenciaUnitariaTests
{
    [Theory]
    [InlineData("Violation of UNIQUE KEY constraint 'uq_cotizaciones_folio'. Cannot insert duplicate key in object 'dbo_cotifly.Cotizaciones'. The duplicate key value is (PCN-1).", "uq_cotizaciones_folio")]
    [InlineData("Cannot insert duplicate key row in object 'dbo_cotifly.UsuariosWeb' with unique index 'uq_usuariosWeb_nombreUsuario'. The duplicate key value is (ana).", "uq_usuariosWeb_nombreUsuario")]
    [InlineData("Infracción de la restricción UNIQUE KEY 'uq_cotizaciones_folio'. No se puede insertar una clave duplicada en el objeto 'dbo_cotifly.Cotizaciones'.", "uq_cotizaciones_folio")]
    [InlineData("Mensaje sin nombre de restricción", null)]
    public void ExtraerRestriccionEncuentraElNombreEnElMensajeDeSqlServer(string mensaje, string? esperado)
    {
        clsUnitOfWork.ExtraerRestriccion(mensaje).Should().Be(esperado);
    }

    [Fact]
    public void ElConvertidorDeEnumsUsaLosCodigosDelEsquema()
    {
        var convertidor = new clsConvertidorCodigoEnum<RolInterno>();

        convertidor.ConvertToProvider(RolInterno.GerenteComercial).Should().Be("gerente_comercial");
        convertidor.ConvertFromProvider("comercial").Should().Be(RolInterno.Comercial);
    }

    [Fact]
    public void ElEstadoDeConversacionSeSerializaEnCamelCaseYSinNulos()
    {
        var estado = clsEstadoConversacion.Vacio with { FolioEnProgreso = "PCN-NV-24092026-001", Version = 1 };

        var json = clsConvertidorEstadoConversacion.Serializar(estado);

        json.Should().Be("""{"folioEnProgreso":"PCN-NV-24092026-001","version":1}""");
        clsConvertidorEstadoConversacion.Deserializar(json).Should().Be(estado);
    }

    // HU-013.9 (#105): el JSON del slot-filling legado (sesiones antiguas de Google Chat) se lee sin fallar; los campos retirados se ignoran.
    [Fact]
    public void ElJsonDelSlotFillingLegadoSeLeeIgnorandoLosCamposRetirados()
    {
        const string Legado =
            """{"nitCliente":"900123456","origenCliente":"prospecto","candidatosCliente":[{"nitCliente":"800","nombre":"Otro","origenCliente":"cliente"}],"origenMargen":"sap_tm","precioFinal":3352941,"trmVigenciaDesde":"2026-09-26","folioEnProgreso":"PCN-NV-24092026-001","cotizacionId":"6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d","moneda":"USD"}""";

        clsConvertidorEstadoConversacion.Deserializar(Legado).Should().Be(clsEstadoConversacion.Vacio with
        {
            FolioEnProgreso = "PCN-NV-24092026-001",
            CotizacionId = Guid.Parse("6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d"),
            Moneda = "USD",
        });
    }

    [Fact]
    public void ElModeloUsaElEsquemaDboCotiflyYLasTablasEnPascalCase()
    {
        using var proveedor = clsServiciosPruebas.Crear(null, new clsRelojFalso());
        using var ambito = proveedor.CreateScope();
        var modelo = ambito.ServiceProvider.GetRequiredService<clsCotiflyDbContext>().Model;

        modelo.GetDefaultSchema().Should().Be("dbo_cotifly");
        modelo.FindEntityType(typeof(clsCotizacionSolicitud))!.GetTableName().Should().Be("CotizacionesSolicitud");
        modelo.FindEntityType(typeof(clsMargenPolitica))!.GetTableName().Should().Be("MargenesPolitica");
        modelo.GetEntityTypes().Should().HaveCount(23, "HU-021: las 20 tablas tras el 0009 (cabecera, rutas, camiones, mercancías y conexos de la cotización) más CapturasIdempotentes del 06 (#191) y SapMargenCliente y SapMargenSyncLog del 17 (HU-042.2, #243)");
    }

    [Fact]
    public void ElContextoUsaNivelDeCompatibilidadDeSqlServer2019()
    {
        using var proveedor = clsServiciosPruebas.Crear(null, new clsRelojFalso());
        using var ambito = proveedor.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<clsCotiflyDbContext>();

        // EF1001: API interna de EF, aceptable solo en esta prueba para leer la opción configurada.
#pragma warning disable EF1001
        var extension = contexto.GetService<IDbContextOptions>()
            .FindExtension<Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal.SqlServerOptionsExtension>();

        extension!.SqlServerCompatibilityLevel.Should().Be(150);
#pragma warning restore EF1001
    }

    [Fact]
    public void ElCheckDeEstadoCosteoCubreExactamenteLosCodigosDelEnum()
    {
        using var proveedor = clsServiciosPruebas.Crear(null, new clsRelojFalso());
        using var ambito = proveedor.CreateScope();
        var modelo = ambito.ServiceProvider.GetRequiredService<clsCotiflyDbContext>().GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;

        var sql = modelo.FindEntityType(typeof(clsCotizacionSolicitud))!.GetCheckConstraints().Single(c => c.Name == "ck_cotizaciones_estadoCosteo").Sql;
        var codigos = System.Text.RegularExpressions.Regex.Matches(sql, "'([a-z_]+)'").Select(m => m.Groups[1].Value);

        codigos.Should().BeEquivalentTo(Cotifly.Dominio.Comun.clsCodigoEnum.Codigos<Cotifly.Dominio.Cotizaciones.EstadoCosteo>());
    }
}
