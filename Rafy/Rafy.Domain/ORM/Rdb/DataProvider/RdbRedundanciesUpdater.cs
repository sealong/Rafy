﻿/*******************************************************
 * 
 * 作者：胡庆访
 * 创建日期：20150314
 * 运行环境：.NET 4.5
 * 版本号：1.0.0
 * 
 * 历史记录：
 * 创建文件 胡庆访 20150314 00:31
 * 
*******************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Rafy.ManagedProperty;

namespace Rafy.Domain.ORM
{
    class RdbRedundanciesUpdater : RedundanciesUpdater
    {
        private RdbDataProvider _dataProvider;

        public RdbRedundanciesUpdater(RdbDataProvider dataProvider)
            : base(dataProvider)
        {
            _dataProvider = dataProvider;
        }

        protected override void UpdateRedundancy(Entity entity, ConcreteProperty redundancy, object newValue, IList<ConcreteProperty> refPathes, object lastRefId)
        {
            /*********************** 代码块解释 *********************************
             * 
             * 假定场景是：
             * refPathes: D(CRef,AName) -> C(BRef) -> B(ARef)，
             * lastRefId: AId。（B.ARef 是最后一个引用属性）
             * 则对应生成的 SQL 是：
             * update D set AName = @AName where CId in (
             *     select id from C where BId in (
             *          select id from B where AId = @AId
             *     )
             * )
             * 如果 B、C 表也有冗余属性，对应的 SQL 则是：
             * update B Set AName = @AName where AId = @BId
             * update C set AName = @AName where BId in (
             *     select id from B where AId = @AId
             * )
             * 
            **********************************************************************/

            //准备所有用到的 DbTable
            var table = RdbTableFinder.TableFor(redundancy.Owner);
            var refTables = new RefPropertyTable[refPathes.Count];
            for (int i = 0, c = refPathes.Count; i < c; i++)
            {
                var refProperty = refPathes[i];
                var refTable = RdbTableFinder.TableFor(refProperty.Owner);
                if (refTable == null)
                {
                    ORMHelper.ThrowBasePropertyNotMappedException(refProperty.Name, refProperty.Owner);
                }

                refTables[i] = new RefPropertyTable
                {
                    RefProperty = refProperty,
                    OwnerTable = refTable
                };
            }

            var sql = new ConditionalSql();
            //SQL: UPDATE D SET AName = {0} WHERE
            sql.Append("UPDATE ").AppendQuoteName(table)
                .Append(" SET ").AppendQuote(table, table.Translate(redundancy.Property))
                .Append(" = ").AppendParameter(newValue).Append(" WHERE ");

            int quoteNeeded = 0;
            if (refTables.Length > 1)
            {
                //中间的都生成 Where XX in
                var inWherePathes = refTables.Take(refTables.Length - 1).ToArray();
                for (int i = 0; i < inWherePathes.Length; i++)
                {
                    var inRef = inWherePathes[i];

                    //SQL: CId In (
                    sql.AppendQuote(table, inRef.OwnerTable.Translate(inRef.RefProperty.Property))
                        .Append(" IN (").AppendLine();
                    quoteNeeded++;

                    var nextRef = refTables[i + 1];

                    //SQL: SELECT Id FROM C WHERE 
                    var nextRefOwnerTable = nextRef.OwnerTable;
                    sql.Append(" SELECT ").AppendQuote(nextRefOwnerTable, nextRefOwnerTable.PKColumn.Name)
                        .Append(" FROM ").AppendQuoteName(nextRefOwnerTable)
                        .Append(" WHERE ");
                }
            }

            //最后一个，生成SQL: BId = {1}
            var lastRef = refTables[refTables.Length - 1];
            sql.AppendQuote(table, lastRef.OwnerTable.Translate(lastRef.RefProperty.Property))
                .Append(" = ").AppendParameter(lastRefId);

            while (quoteNeeded > 0)
            {
                sql.AppendLine(")");
                quoteNeeded--;
            }

            //执行最终的 SQL 语句
            using (var dba = _dataProvider.CreateDbAccesser())
            {
                dba.ExecuteText(sql, sql.Parameters);
            }
        }

        /// <summary>
        /// 某个引用属性与其所在类对应的表元数据
        /// </summary>
        private struct RefPropertyTable
        {
            public ConcreteProperty RefProperty;
            public RdbTable OwnerTable;
        }
    }
}
